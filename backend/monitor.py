"""
Hardware telemetry WebSocket broadcaster.

- Polls CPU / RAM / Disk via psutil
- Polls NVIDIA GPU (temp, util, mem, power) via pynvml
- Spawns sensors.exe (LibreHardwareMonitor wrapper, requires admin) for CPU temp/power
- Broadcasts JSON to all connected clients every 1s on ws://localhost:8765
"""
import asyncio
import json
import logging
import os
import platform
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

import psutil
import websockets
from websockets.server import WebSocketServerProtocol

try:
    from pynvml import (
        NVMLError,
        nvmlDeviceGetCount,
        nvmlDeviceGetHandleByIndex,
        nvmlDeviceGetMemoryInfo,
        nvmlDeviceGetName,
        nvmlDeviceGetPowerUsage,
        nvmlDeviceGetTemperature,
        nvmlDeviceGetUtilizationRates,
        nvmlInit,
        nvmlShutdown,
        NVML_TEMPERATURE_GPU,
    )
    NVML_AVAILABLE = True
    _NVML_IMPORT_ERROR: str | None = None
except ImportError as _exc:
    NVML_AVAILABLE = False
    _NVML_IMPORT_ERROR = str(_exc)

# Detected at runtime once a device handle is in hand (see init_gpu).
_NVML_HAS_V2: bool = False


def _prime_nvml_dll_search_path() -> None:
    """Make ctypes able to load nvml.dll on systems where the default DLL
    search path can't resolve all of its NVIDIA dependencies.

    System32 is always on the search path, but nvml.dll in System32 is often
    just a stub that delegates to versioned implementations under the
    DriverStore (C:\\Windows\\System32\\DriverStore\\FileRepository\\nv*\\...).
    If those sibling DLLs aren't reachable, LoadLibrary fails and pynvml
    reports 'NVML Shared Library Not Found'.

    We:
      1. Add every plausible NVIDIA bin directory to the DLL search path so
         nvml.dll's dependencies resolve.
      2. Explicitly preload nvml.dll by full path. This both surfaces a real
         OSError (instead of pynvml's opaque 'not found') and warms the
         process loader, so pynvml's later CDLL("nvml.dll") just reuses it.
    """
    import glob
    candidates: list[str] = [
        r"C:\Program Files\NVIDIA Corporation\NVSMI",
        r"C:\Windows\System32",
        r"C:\Windows\SysWOW64",
    ]
    try:
        candidates.extend(glob.glob(
            r"C:\Windows\System32\DriverStore\FileRepository\nv*amd64*"))
    except Exception:
        pass

    chosen_dll: str | None = None
    seen: set[str] = set()
    for p in candidates:
        if p in seen or not os.path.isdir(p):
            continue
        seen.add(p)
        try:
            os.add_dll_directory(p)
        except Exception:
            os.environ["PATH"] = p + os.pathsep + os.environ.get("PATH", "")
        if chosen_dll is None:
            f = os.path.join(p, "nvml.dll")
            if os.path.isfile(f):
                chosen_dll = f

    if chosen_dll is None:
        log.warning("nvml.dll not found in any known NVIDIA path")
        return
    # pynvml 12.x hardcodes nvml.dll's path to %ProgramFiles%\NVIDIA Corporation\
    # NVSMI\nvml.dll — a folder modern (2023+) NVIDIA drivers no longer create.
    # Workaround: load nvml.dll ourselves with cdecl convention and inject the
    # handle into pynvml.nvmlLib. pynvml's loader does `if nvmlLib is None`,
    # so a pre-set value short-circuits its broken path lookup.
    try:
        import ctypes
        nvml = ctypes.CDLL(chosen_dll)
        try:
            import pynvml as _pynvml
            _pynvml.nvmlLib = nvml
            log.info("Injected nvml.dll into pynvml from %s", chosen_dll)
        except Exception as exc:
            log.warning("nvmlLib injection failed: %s", exc)
    except OSError as exc:
        log.warning("Loading %s with cdecl failed: %s", chosen_dll, exc)

HOST = "localhost"
PORT = 8765
POLL_INTERVAL_SEC = 1.0


def _setup_logging() -> logging.Logger:
    """Log to %LOCALAPPDATA%\\PC Monitor\\monitor-debug.log when packaged.
    PyInstaller --noconsole (windowed mode) discards stderr/stdout, so without
    a file handler we'd be flying blind in the field. In dev (running directly
    via `python monitor.py`) we also keep a stream handler.
    """
    fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")
    root = logging.getLogger("monitor")
    root.setLevel(logging.INFO)
    root.propagate = False

    if not any(isinstance(h, logging.StreamHandler) for h in root.handlers):
        sh = logging.StreamHandler()
        sh.setFormatter(fmt)
        root.addHandler(sh)

    try:
        local_appdata = os.environ.get("LOCALAPPDATA")
        if local_appdata:
            log_dir = Path(local_appdata) / "PC Monitor"
            log_dir.mkdir(parents=True, exist_ok=True)
            fh = logging.FileHandler(log_dir / "monitor-debug.log", mode="w", encoding="utf-8")
            fh.setFormatter(fmt)
            root.addHandler(fh)
    except Exception:
        pass
    return root


log = _setup_logging()
log.info("monitor.py starting; NVML_AVAILABLE=%s NVML_HAS_V2=%s",
         NVML_AVAILABLE, _NVML_HAS_V2)
if not NVML_AVAILABLE and _NVML_IMPORT_ERROR:
    log.warning("pynvml import failed: %s", _NVML_IMPORT_ERROR)

CLIENTS: set[WebSocketServerProtocol] = set()

# Latest snapshot from sensors.exe sidecar (LibreHardwareMonitor).
# Stale-out after SENSORS_TTL_SEC if the subprocess stops reporting.
SENSORS_TTL_SEC = 5.0
_sensors_state: dict[str, Any] = {
    "cpu_temperature": None,
    "cpu_power": None,
    "cpu_power_source": None,
    "source": None,
    "updated_at": 0.0,
    # System identity (sensors.exe re-emits the same values every tick)
    "system_cpu": None,
    "system_motherboard": None,
    # WDDM GPU snapshot (cross-vendor; null when sensors.exe can't read it)
    "gpu_name": None,
    "gpu_utilization": None,
    "gpu_memory_dedicated_mb": None,
    "gpu_memory_shared_mb": None,
    "gpu_memory_dedicated_total_mb": None,
    "gpu_source": None,
}


def find_sensors_exe() -> Path | None:
    """Locate sensors.exe (the LHM helper).

    - Packaged (PyInstaller frozen, run by Tauri): same dir as monitor.exe.
    - Dev: backend/sensors-cs/publish/sensors.exe.
    """
    candidates: list[Path] = []
    if getattr(sys, "frozen", False):
        candidates.append(Path(sys.executable).parent / "sensors.exe")
    here = Path(__file__).resolve().parent
    candidates.append(here / "sensors-cs" / "publish" / "sensors.exe")
    candidates.append(here / "sensors.exe")
    for p in candidates:
        if p.exists():
            return p
    return None


async def sensors_subprocess_loop() -> None:
    """Spawn sensors.exe and consume its NDJSON output indefinitely."""
    exe = find_sensors_exe()
    if exe is None:
        log.info("sensors.exe not found; CPU temperature will be unavailable")
        return

    while True:
        log.info("Spawning sensors.exe at %s", exe)
        try:
            creationflags = 0x08000000 if os.name == "nt" else 0
            proc = await asyncio.create_subprocess_exec(
                str(exe),
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                creationflags=creationflags,
            )
        except Exception as exc:
            log.warning("Failed to spawn sensors.exe: %s", exc)
            await asyncio.sleep(5.0)
            continue

        assert proc.stdout is not None
        try:
            while True:
                line = await proc.stdout.readline()
                if not line:
                    break
                try:
                    data = json.loads(line.decode("utf-8", errors="ignore").strip())
                except json.JSONDecodeError:
                    continue
                if "error" in data:
                    log.warning("sensors.exe reported: %s", data["error"])
                    continue
                _sensors_state["cpu_temperature"] = data.get("cpu_temperature")
                _sensors_state["cpu_power"] = data.get("cpu_power")
                _sensors_state["cpu_power_source"] = data.get("cpu_power_source")
                _sensors_state["source"] = data.get("source")
                _sensors_state["system_cpu"] = data.get("system_cpu") or _sensors_state["system_cpu"]
                _sensors_state["system_motherboard"] = (
                    data.get("system_motherboard") or _sensors_state["system_motherboard"]
                )
                _sensors_state["gpu_name"] = data.get("gpu_name")
                _sensors_state["gpu_utilization"] = data.get("gpu_utilization")
                _sensors_state["gpu_memory_dedicated_mb"] = data.get("gpu_memory_dedicated_mb")
                _sensors_state["gpu_memory_shared_mb"] = data.get("gpu_memory_shared_mb")
                _sensors_state["gpu_memory_dedicated_total_mb"] = data.get("gpu_memory_dedicated_total_mb")
                _sensors_state["gpu_source"] = data.get("gpu_source")
                _sensors_state["updated_at"] = time.time()
        except Exception as exc:
            log.warning("Error reading sensors.exe stdout: %s", exc)
        finally:
            try:
                proc.kill()
            except Exception:
                pass
            try:
                await proc.wait()
            except Exception:
                pass

        log.info("sensors.exe exited; restarting in 3s")
        await asyncio.sleep(3.0)


def get_lhm_snapshot() -> dict[str, Any]:
    """Return current LHM-derived values, or null if stale."""
    if time.time() - _sensors_state["updated_at"] > SENSORS_TTL_SEC:
        return {
            "cpu_temperature": None,
            "cpu_power": None,
            "cpu_power_source": None,
            "source": None,
        }
    return {
        "cpu_temperature": _sensors_state["cpu_temperature"],
        "cpu_power": _sensors_state["cpu_power"],
        "cpu_power_source": _sensors_state["cpu_power_source"],
        "source": _sensors_state["source"],
    }


def _safe_decode(name: Any) -> str:
    return name.decode() if isinstance(name, bytes) else str(name)


def detect_cpu_name() -> str:
    """Read the CPU brand string from the registry.

    Used as a backstop when sensors.exe's WMI Win32_Processor query returns
    nothing usable (rare, but happens on some custom/server BIOS images).
    HKLM\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0\\ProcessorNameString
    is populated by the kernel at boot from CPUID brand-string leaves and is
    extremely reliable.
    """
    if os.name != "nt":
        try:
            return platform.processor() or "Unknown CPU"
        except Exception:
            return "Unknown CPU"
    try:
        import winreg  # type: ignore
        with winreg.OpenKey(
            winreg.HKEY_LOCAL_MACHINE,
            r"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
        ) as key:
            val, _ = winreg.QueryValueEx(key, "ProcessorNameString")
            return val.strip() or "Unknown CPU"
    except Exception:
        return platform.processor() or "Unknown CPU"


_CPU_NAME_FALLBACK: str | None = None


def _format_disk_type(media_type: str, bus_type: str) -> str:
    """Map (MediaType, BusType) -> friendly label. Examples:
        ("SSD", "NVMe") -> "NVMe SSD"
        ("SSD", "SATA") -> "SATA SSD"
        ("HDD", "SATA") -> "HDD"
        ("",    "USB")  -> "USB"
    """
    bt = (bus_type or "").strip()
    mt = (media_type or "").strip()
    if mt == "SSD":
        return f"{bt} SSD" if bt and bt not in ("SSD",) else "SSD"
    if mt == "HDD":
        return f"{bt} HDD" if bt == "USB" else "HDD"
    if bt:
        return bt
    return "Storage"


def detect_disk_topology() -> list[dict[str, Any]]:
    """Build a per-physical-disk topology by running Get-PhysicalDisk +
    Get-Partition once at startup, mapping partitions to their parent disk.

    Returns a list ordered by DiskNumber:
        [{ "diskNumber": 0, "model": "...", "type": "NVMe SSD",
           "totalBytes": 512000000000, "driveLetters": ["C:"] }, ...]

    Drives without any drive-letter partition (raw / unformatted / system-
    reserved-only) are still included but with an empty driveLetters list.
    """
    if os.name != "nt":
        return [{
            "diskNumber": 0, "model": "rootfs", "type": "Storage",
            "totalBytes": 0, "driveLetters": ["/"],
        }]
    try:
        ps_cmd = (
            "$disks = Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, "
            "MediaType, BusType, Size; "
            "$parts = Get-Partition | Select-Object DiskNumber, DriveLetter; "
            "@{disks=$disks; partitions=$parts} | ConvertTo-Json -Compress -Depth 4"
        )
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", ps_cmd],
            capture_output=True, text=True, timeout=15,
            creationflags=0x08000000,
        )
        if result.returncode != 0 or not result.stdout.strip():
            return []
        data = json.loads(result.stdout)
        disks_raw = data.get("disks") or []
        parts_raw = data.get("partitions") or []
        if isinstance(disks_raw, dict):
            disks_raw = [disks_raw]
        if isinstance(parts_raw, dict):
            parts_raw = [parts_raw]

        # Map disk number -> list of drive letters
        letters_by_disk: dict[int, list[str]] = {}
        for p in parts_raw:
            try:
                n = int(p.get("DiskNumber"))
            except (TypeError, ValueError):
                continue
            dl = p.get("DriveLetter")
            if not dl:
                continue
            letters_by_disk.setdefault(n, []).append(f"{str(dl)[0]}:")

        topology: list[dict[str, Any]] = []
        for d in disks_raw:
            try:
                n = int(d.get("DeviceId"))
            except (TypeError, ValueError):
                continue
            topology.append({
                "diskNumber": n,
                "model": str(d.get("FriendlyName") or "").strip() or "Disk",
                "type": _format_disk_type(
                    str(d.get("MediaType") or ""),
                    str(d.get("BusType") or ""),
                ),
                "totalBytes": int(d.get("Size") or 0),
                "driveLetters": sorted(set(letters_by_disk.get(n, []))),
            })
        topology.sort(key=lambda x: x["diskNumber"])
        return topology
    except Exception as exc:
        log.debug("Disk topology detection failed: %s", exc)
        return []


_DISK_TOPOLOGY: list[dict[str, Any]] = []


def init_gpu() -> int | None:
    global _NVML_HAS_V2
    if not NVML_AVAILABLE:
        log.warning("pynvml not installed; GPU telemetry disabled")
        return None
    _prime_nvml_dll_search_path()
    try:
        nvmlInit()
        if nvmlDeviceGetCount() == 0:
            log.warning("No NVIDIA GPU detected")
            return None
        handle = nvmlDeviceGetHandleByIndex(0)
        log.info("GPU detected: %s", _safe_decode(nvmlDeviceGetName(handle)))
        # Probe v2 memory API once with this handle. v2 returns the full
        # installed FB total (incl. driver-reserved area), avoiding the v1
        # under-report on cards >4 GB.
        try:
            nvmlDeviceGetMemoryInfo(handle, version=2)
            _NVML_HAS_V2 = True
        except (TypeError, NVMLError) as ex:
            log.info("NVML memory v2 unavailable, using v1: %s", ex)
            _NVML_HAS_V2 = False
        log.info("NVML memory API: %s", "v2" if _NVML_HAS_V2 else "v1")
        return 0
    except NVMLError as exc:
        log.warning("NVML init failed: %s", exc)
        return None


def read_gpu(index: int | None) -> dict[str, Any]:
    if index is not None:
        try:
            handle = nvmlDeviceGetHandleByIndex(index)
            util = nvmlDeviceGetUtilizationRates(handle)
            # Prefer v2: returns the full installed FB total (incl. driver-
            # reserved area). v1 underreports VRAM on cards >4 GB on some
            # Windows driver builds.
            mem = nvmlDeviceGetMemoryInfo(handle, version=2) if _NVML_HAS_V2 else nvmlDeviceGetMemoryInfo(handle)
            temp = nvmlDeviceGetTemperature(handle, NVML_TEMPERATURE_GPU)
            try:
                power_w = nvmlDeviceGetPowerUsage(handle) / 1000.0
            except NVMLError:
                power_w = None
            return {
                "available": True,
                "name": _safe_decode(nvmlDeviceGetName(handle)),
                "utilization": util.gpu,
                "memoryUtilization": round(mem.used / mem.total * 100, 1),
                "memoryUsedMb": round(mem.used / 1024 / 1024),
                "memoryTotalMb": round(mem.total / 1024 / 1024),
                "temperature": temp,
                "powerWatts": power_w,
                "source": "nvml",
            }
        except NVMLError as exc:
            log.warning("GPU read failed: %s", exc)
            # Fall through to WDDM rather than returning unavailable

    # No NVIDIA GPU (or NVML failed) — try WDDM perf-counter data from sensors.exe.
    return read_gpu_wddm()


def read_gpu_wddm() -> dict[str, Any]:
    """Return GPU telemetry from sensors.exe's WDDM poller.

    Used as a cross-vendor fallback when NVML isn't available (Intel/AMD iGPU,
    AMD/Intel discrete cards). Temperature and power are not available via
    perf counters — for an iGPU those readings live in the CPU package anyway.
    """
    if time.time() - _sensors_state["updated_at"] > SENSORS_TTL_SEC:
        return {"available": False}
    name = _sensors_state.get("gpu_name")
    util = _sensors_state.get("gpu_utilization")
    if not name and util is None:
        return {"available": False}

    dedicated_mb = _sensors_state.get("gpu_memory_dedicated_mb") or 0
    shared_mb = _sensors_state.get("gpu_memory_shared_mb") or 0
    dedicated_total_mb = _sensors_state.get("gpu_memory_dedicated_total_mb") or 0

    # iGPUs report ~0 dedicated; show shared usage instead. Discrete GPUs report
    # both — prefer dedicated for the gauge since that's the on-card VRAM.
    is_integrated = dedicated_total_mb < 256  # < 256 MB reserved → almost certainly an iGPU
    used_mb = dedicated_mb if not is_integrated else shared_mb
    total_mb = dedicated_total_mb if not is_integrated else 0
    mem_util_pct = round(used_mb / total_mb * 100, 1) if total_mb else None

    return {
        "available": True,
        "name": name or "GPU",
        "utilization": util,
        "memoryUtilization": mem_util_pct,
        "memoryUsedMb": used_mb,
        "memoryTotalMb": total_mb if total_mb else None,
        "temperature": None,
        "powerWatts": None,
        "source": "wddm",
        "integrated": is_integrated,
    }


def read_cpu_temperature() -> float | None:
    """psutil.sensors_temperatures is unreliable on Windows. Try anyway."""
    fn = getattr(psutil, "sensors_temperatures", None)
    if fn is None:
        return None
    try:
        temps = fn()
    except Exception:
        return None
    for label in ("k10temp", "coretemp", "cpu_thermal", "acpitz"):
        if label in temps and temps[label]:
            return temps[label][0].current
    for entries in temps.values():
        if entries:
            return entries[0].current
    return None


def read_disks() -> list[dict[str, Any]]:
    """Per-physical-disk usage and I/O. Topology is cached at startup; per-tick
    data is psutil-fast.

    For each physical disk we sum partition usage across its drive letters,
    pair with cumulative read/write byte counts from
    psutil.disk_io_counters(perdisk=True) keyed `PhysicalDriveN`, and surface
    the cached model/type/total from the topology probe.
    """
    if not _DISK_TOPOLOGY:
        return []

    # Map mountpoint -> disk index (for letters present in topology only)
    mp_to_disk: dict[str, int] = {}
    for d in _DISK_TOPOLOGY:
        for letter in d.get("driveLetters", []):
            mp_to_disk[f"{letter[0].upper()}:\\"] = d["diskNumber"]

    used_by_disk: dict[int, int] = {}
    cap_by_disk: dict[int, int] = {}
    if psutil.WINDOWS:
        for p in psutil.disk_partitions(all=False):
            if "cdrom" in p.opts or p.fstype == "":
                continue
            n = mp_to_disk.get(p.mountpoint.upper())
            if n is None:
                continue
            try:
                u = psutil.disk_usage(p.mountpoint)
            except (PermissionError, OSError):
                continue
            used_by_disk[n] = used_by_disk.get(n, 0) + u.used
            cap_by_disk[n] = cap_by_disk.get(n, 0) + u.total

    # Per-disk I/O byte totals
    perdisk_io = {}
    try:
        perdisk_io = psutil.disk_io_counters(perdisk=True) or {}
    except Exception:
        pass

    out: list[dict[str, Any]] = []
    for d in _DISK_TOPOLOGY:
        n = d["diskNumber"]
        used = used_by_disk.get(n, 0)
        # Fall back to topology size when no partition is mounted (still show
        # the disk so the user sees it exists).
        cap = cap_by_disk.get(n) or d.get("totalBytes") or 0
        pct = round(used / cap * 100, 1) if cap else 0.0

        io = perdisk_io.get(f"PhysicalDrive{n}")
        out.append({
            "diskNumber": n,
            "driveLetters": d.get("driveLetters", []),
            "model": d.get("model", "Disk"),
            "type": d.get("type", "Storage"),
            "percent": pct,
            "usedGb": round(used / 1024**3, 1),
            "totalGb": round(cap / 1024**3, 1) if cap else 0.0,
            "readMb": round(io.read_bytes / 1024**2, 1) if io else None,
            "writeMb": round(io.write_bytes / 1024**2, 1) if io else None,
        })
    return out


def build_payload(gpu_index: int | None) -> dict[str, Any]:
    cpu_percent = psutil.cpu_percent(interval=None)
    mem = psutil.virtual_memory()
    lhm = get_lhm_snapshot()
    cpu_temp = lhm["cpu_temperature"]
    cpu_temp_source = lhm["source"]
    if cpu_temp is None:
        cpu_temp = read_cpu_temperature()
        if cpu_temp is not None:
            cpu_temp_source = "psutil"
    gpu = read_gpu(gpu_index)
    # Prefer sensors.exe's WMI value but ignore its "?" placeholder; fall back
    # to the registry CPU brand string we cached at startup.
    sensor_cpu = (_sensors_state.get("system_cpu") or "").strip().lstrip("?").strip()
    cpu_name = sensor_cpu or _CPU_NAME_FALLBACK or "Unknown CPU"
    # Pick the system-drive's type for the header. Disk holding C: is the
    # natural choice; fall back to the first disk if C: isn't found.
    system_storage_type = "Storage"
    if _DISK_TOPOLOGY:
        sys_disk = next(
            (d for d in _DISK_TOPOLOGY if any(l.upper() == "C:" for l in d.get("driveLetters", []))),
            _DISK_TOPOLOGY[0],
        )
        system_storage_type = sys_disk.get("type") or "Storage"
    return {
        "timestamp": time.time(),
        "systemInfo": {
            "cpu": cpu_name,
            "gpu": gpu.get("name") if gpu.get("available") else None,
            "storage": system_storage_type,
        },
        "cpu": {
            "utilization": cpu_percent,
            "perCore": psutil.cpu_percent(interval=None, percpu=True),
            "frequencyMhz": round(psutil.cpu_freq().current) if psutil.cpu_freq() else None,
            "temperature": cpu_temp,
            "temperatureSource": cpu_temp_source,
            "powerWatts": lhm["cpu_power"],
            "powerSource": lhm["cpu_power_source"],
        },
        "memory": {
            "percent": mem.percent,
            "usedGb": round(mem.used / 1024**3, 1),
            "totalGb": round(mem.total / 1024**3, 1),
        },
        "gpu": gpu,
        "disks": read_disks(),
    }


async def register(ws: WebSocketServerProtocol) -> None:
    CLIENTS.add(ws)
    log.info("Client connected (%d total)", len(CLIENTS))
    try:
        await ws.wait_closed()
    finally:
        CLIENTS.discard(ws)
        log.info("Client disconnected (%d total)", len(CLIENTS))


async def broadcast_loop(gpu_index: int | None) -> None:
    psutil.cpu_percent(interval=None)
    while True:
        if CLIENTS:
            payload = json.dumps(build_payload(gpu_index))
            await asyncio.gather(
                *(c.send(payload) for c in CLIENTS),
                return_exceptions=True,
            )
        await asyncio.sleep(POLL_INTERVAL_SEC)


async def main() -> None:
    global _DISK_TOPOLOGY, _CPU_NAME_FALLBACK
    gpu_index = init_gpu()
    _DISK_TOPOLOGY = detect_disk_topology()
    _CPU_NAME_FALLBACK = detect_cpu_name()
    log.info("Disk topology: %d disks found", len(_DISK_TOPOLOGY))
    for d in _DISK_TOPOLOGY:
        log.info("  Disk %s: %s (%s) %.1f GB letters=%s",
                 d.get("diskNumber"), d.get("model"), d.get("type"),
                 (d.get("totalBytes") or 0) / 1024**3, d.get("driveLetters"))
    log.info("CPU name fallback: %s", _CPU_NAME_FALLBACK)
    sensors_task: asyncio.Task | None = None
    try:
        try:
            server = await websockets.serve(register, HOST, PORT)
        except OSError as exc:
            if getattr(exc, "errno", None) in (10048, 98) or "10048" in str(exc):
                log.info("Port %d already in use; another monitor instance is serving. Exiting.", PORT)
                return
            raise

        sensors_task = asyncio.create_task(sensors_subprocess_loop())

        async with server:
            log.info("WebSocket server listening on ws://%s:%d", HOST, PORT)
            await broadcast_loop(gpu_index)
    finally:
        if sensors_task is not None and not sensors_task.done():
            sensors_task.cancel()
            try:
                await sensors_task
            except (asyncio.CancelledError, Exception):
                pass
        if NVML_AVAILABLE and gpu_index is not None:
            try:
                nvmlShutdown()
            except NVMLError:
                pass


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        log.info("Shutting down")
    except Exception as exc:
        # PyInstaller --noconsole shows a popup dialog on unhandled exceptions.
        # Swallow them so a packaged sidecar exits silently if anything goes wrong.
        log.exception("Fatal error: %s", exc)
