"""
Hardware telemetry WebSocket broadcaster.

- Polls CPU / RAM / Disk via psutil
- Polls NVIDIA GPU (temp, util, mem, power) via pynvml
- Spawns sensors.exe (LibreHardwareMonitor wrapper, requires admin) for CPU temp/power/fans
- Broadcasts JSON to all connected clients every 1s on ws://localhost:8765
"""
import asyncio
import json
import logging
import os
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
except ImportError:
    NVML_AVAILABLE = False

HOST = "localhost"
PORT = 8765
POLL_INTERVAL_SEC = 1.0

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("monitor")

CLIENTS: set[WebSocketServerProtocol] = set()

# Latest snapshot from sensors.exe sidecar (LibreHardwareMonitor).
# Stale-out after SENSORS_TTL_SEC if the subprocess stops reporting.
SENSORS_TTL_SEC = 5.0
_sensors_state: dict[str, Any] = {
    "cpu_temperature": None,
    "cpu_power": None,
    "fan_rpm": None,
    "updated_at": 0.0,
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
                _sensors_state["fan_rpm"] = data.get("fan_rpm")
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
        return {"cpu_temperature": None, "cpu_power": None, "fan_rpm": None}
    return {
        "cpu_temperature": _sensors_state["cpu_temperature"],
        "cpu_power": _sensors_state["cpu_power"],
        "fan_rpm": _sensors_state["fan_rpm"],
    }


def _safe_decode(name: Any) -> str:
    return name.decode() if isinstance(name, bytes) else str(name)


def init_gpu() -> int | None:
    if not NVML_AVAILABLE:
        log.warning("pynvml not installed; GPU telemetry disabled")
        return None
    try:
        nvmlInit()
        if nvmlDeviceGetCount() == 0:
            log.warning("No NVIDIA GPU detected")
            return None
        handle = nvmlDeviceGetHandleByIndex(0)
        log.info("GPU detected: %s", _safe_decode(nvmlDeviceGetName(handle)))
        return 0
    except NVMLError as exc:
        log.warning("NVML init failed: %s", exc)
        return None


def read_gpu(index: int | None) -> dict[str, Any]:
    if index is None:
        return {"available": False}
    try:
        handle = nvmlDeviceGetHandleByIndex(index)
        util = nvmlDeviceGetUtilizationRates(handle)
        mem = nvmlDeviceGetMemoryInfo(handle)
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
        }
    except NVMLError as exc:
        log.warning("GPU read failed: %s", exc)
        return {"available": False, "error": str(exc)}


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


def read_disk() -> dict[str, Any]:
    usage = psutil.disk_usage("C:\\" if psutil.WINDOWS else "/")
    io = psutil.disk_io_counters()
    return {
        "percent": usage.percent,
        "usedGb": round(usage.used / 1024**3, 1),
        "totalGb": round(usage.total / 1024**3, 1),
        "readMb": round(io.read_bytes / 1024**2, 1) if io else None,
        "writeMb": round(io.write_bytes / 1024**2, 1) if io else None,
    }


def build_payload(gpu_index: int | None) -> dict[str, Any]:
    cpu_percent = psutil.cpu_percent(interval=None)
    mem = psutil.virtual_memory()
    lhm = get_lhm_snapshot()
    cpu_temp = lhm["cpu_temperature"]
    if cpu_temp is None:
        cpu_temp = read_cpu_temperature()
    return {
        "timestamp": time.time(),
        "cpu": {
            "utilization": cpu_percent,
            "perCore": psutil.cpu_percent(interval=None, percpu=True),
            "frequencyMhz": round(psutil.cpu_freq().current) if psutil.cpu_freq() else None,
            "temperature": cpu_temp,
            "powerWatts": lhm["cpu_power"],
        },
        "memory": {
            "percent": mem.percent,
            "usedGb": round(mem.used / 1024**3, 1),
            "totalGb": round(mem.total / 1024**3, 1),
        },
        "gpu": read_gpu(gpu_index),
        "disk": read_disk(),
        "fans": {
            "rpm": lhm["fan_rpm"],
        },
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
    gpu_index = init_gpu()
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
