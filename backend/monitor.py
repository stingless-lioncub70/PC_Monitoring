"""
Hardware telemetry WebSocket broadcaster.

- Polls CPU / RAM / Disk via psutil
- Polls NVIDIA GPU (temp, util, mem, power) via pynvml
- Broadcasts JSON to all connected clients every 1s on ws://localhost:8765
"""
import asyncio
import json
import logging
import time
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
    return {
        "timestamp": time.time(),
        "cpu": {
            "utilization": cpu_percent,
            "perCore": psutil.cpu_percent(interval=None, percpu=True),
            "frequencyMhz": round(psutil.cpu_freq().current) if psutil.cpu_freq() else None,
            "temperature": read_cpu_temperature(),
        },
        "memory": {
            "percent": mem.percent,
            "usedGb": round(mem.used / 1024**3, 1),
            "totalGb": round(mem.total / 1024**3, 1),
        },
        "gpu": read_gpu(gpu_index),
        "disk": read_disk(),
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
    try:
        try:
            server = await websockets.serve(register, HOST, PORT)
        except OSError as exc:
            # WSAEADDRINUSE (10048) on Windows / EADDRINUSE on POSIX:
            # another monitor instance is already serving on this port.
            # Exit cleanly so the new sidecar attempt doesn't error-popup.
            if getattr(exc, "errno", None) in (10048, 98) or "10048" in str(exc):
                log.info("Port %d already in use; another monitor instance is serving. Exiting.", PORT)
                return
            raise
        async with server:
            log.info("WebSocket server listening on ws://%s:%d", HOST, PORT)
            await broadcast_loop(gpu_index)
    finally:
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
