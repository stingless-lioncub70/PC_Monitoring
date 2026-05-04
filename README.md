# PC Monitor

A lightweight, real-time Windows hardware monitor. Live CPU, GPU, RAM, and disk telemetry in a single dashboard — built for clarity and cross-vendor compatibility, not Christmas-tree dashboards full of numbers nobody reads.

[![Latest release](https://img.shields.io/github/v/release/Allain-afk/PC_Monitoring?display_name=tag)](https://github.com/Allain-afk/PC_Monitoring/releases/latest)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)](https://github.com/Allain-afk/PC_Monitoring/releases/latest)

---

## Features

- **Live gauges, updated every second** — CPU load / temp / power, GPU load / temp / VRAM, RAM, aggregated disk usage with read/write throughput.
- **Cross-vendor by design.** NVIDIA via NVML (full stats), Intel/AMD/integrated GPUs via WDDM performance counters (load + memory).
- **Multi-tier CPU temperature ladder.** LibreHardwareMonitor (MSR) → Lenovo Legion WMI → Performance Counter thermal zone → ACPI thermal zone → SMBIOS probe. Each tier is silent if unavailable, and the active source is shown beneath the gauge so you know whether you're looking at a real-time MSR read or an EC-cached value.
- **Honest device header.** CPU name, GPU name, and primary storage type (NVMe / SSD / HDD) are auto-detected per machine. No hardcoded labels.
- **Multi-drive disk usage.** Aggregates every fixed local drive — multi-SSD setups report the combined picture, not just C:.
- **iGPU-aware UI.** Integrated GPUs report shared system memory in GB instead of pretending they have dedicated VRAM, and the GPU temp tile shows "shared w/ CPU" since they're on the same die.

## Install

Grab the latest release from the [Releases page](https://github.com/Allain-afk/PC_Monitoring/releases/latest) and download `PC Monitor_*_x64-setup.zip`.

1. Extract the zip.
2. Double-click `PC Monitor_*_x64-setup.exe`.
3. SmartScreen will warn (the binary is unsigned) — click **More info → Run anyway**.
4. Approve the UAC prompt.
5. Launch from the Start Menu. The admin prompt on every launch is expected — the WinRing0 driver needs elevation to read CPU MSRs.

## Hardware compatibility

| Component | Status |
| --- | --- |
| CPU stats (load, frequency) | All x64 Intel / AMD on any vendor |
| CPU temperature | Best on Intel/AMD when WinRing0 is allowed; falls back to vendor WMI / ACPI on locked-down systems |
| Lenovo Legion CPU temp | Reads from `LENOVO_GAMEZONE_DATA` (same source as Lenovo Vantage) |
| NVIDIA GPU | Full stats via NVML — temp, power, util, VRAM (incl. > 4 GB via NVML v2) |
| AMD / Intel discrete + integrated GPU | Util + memory via WDDM perf counters; temp shared with CPU package on iGPU |
| Storage | All fixed local drives detected; primary type identified via `Get-PhysicalDisk` |

## Known limitations

- **NVIDIA only for full GPU details.** Temp and watts come from NVML; AMD / Intel currently show utilisation and memory only.
- **Fan RPM not displayed.** Most consumer hardware doesn't expose it without proprietary OEM software, and the unreliable subset wasn't worth a misleading "—" placeholder.
- **Microsoft Vulnerable Driver Blocklist** can block WinRing0. The app keeps working — CPU temp falls through to ACPI / perf-counter sources, and the gauge sublabel will say so.
- **Windows x64 only.** No macOS or Linux build.

## Architecture

```
+------------------+   stdout (NDJSON)   +-------------------+   WebSocket   +---------------------+
|  sensors.exe     | ------------------> |   monitor.exe     | ------------> |  Tauri dashboard    |
|  (.NET 8 +       |                     |  (Python +        |  ws://8765    |  (React + Vite +    |
|   LHM 0.9.6)     |                     |   psutil + NVML)  |               |   Tailwind)         |
+------------------+                     +-------------------+               +---------------------+
   CPU temp / power                       GPU (NVML or WDDM),                Live gauges, ~1 Hz
   Vendor WMI poller                      memory, disk aggregation,
   WDDM GPU snapshot                      systemInfo composition
   System identity
```

- **`backend/sensors-cs/`** — .NET 8 self-contained executable wrapping LibreHardwareMonitorLib + WMI vendor pollers + WDDM perf counters. Emits one JSON line per second on stdout.
- **`backend/monitor.py`** — Python 3.11 broadcaster. Spawns sensors.exe, polls `psutil` and `pynvml`, merges everything into a single payload, and broadcasts on `ws://localhost:8765`.
- **`pc-monitor/`** — Tauri v2 + React + TypeScript dashboard. Subscribes to the WebSocket and renders the gauges.

## Build from source

Requires:

- Windows 10 / 11 x64
- Rust + Cargo (for Tauri)
- Node.js 18+
- Python 3.11+ with `pip`
- .NET 8 SDK

Once-only setup:

```powershell
# Python deps
python -m pip install -r backend\requirements.txt pyinstaller

# JS deps
cd pc-monitor
npm install
cd ..
```

Build the whole installer in one shot:

```powershell
.\build.ps1
```

This:

1. Publishes `sensors.exe` (`dotnet publish` in `backend/sensors-cs`)
2. Builds `monitor.exe` (`pyinstaller` in `backend`)
3. Stages both into `pc-monitor/src-tauri/binaries/` with the `-x86_64-pc-windows-msvc` triple suffix Tauri requires
4. Runs `npm run tauri -- build`

Output lands in `pc-monitor/src-tauri/target/release/bundle/{msi,nsis}/`.

## Run in dev (no installer)

```powershell
# Terminal 1 — backend
python backend\monitor.py

# Terminal 2 — frontend
cd pc-monitor
npm run tauri dev
```

The dev frontend connects to the same `ws://localhost:8765` and hot-reloads on edits.

## Troubleshooting

If something looks wrong, two log files at `%LOCALAPPDATA%\PC Monitor\` tell us almost everything:

- **`monitor-debug.log`** — Python broadcaster: NVML init result, disk type detection, GPU detection
- **`sensors-debug.log`** — .NET sidecar: full enumeration of every sensor source the machine exposes (Lenovo / ASUS / Dell / HP / MSI WMI namespaces, ACPI thermal zones, LHM hardware tree)

If the CPU Temp sublabel says `ACPI (cached)` and the value lags, the **Microsoft Vulnerable Driver Blocklist** (Windows Security → Device Security → Core Isolation) is likely blocking WinRing0. Disabling it gives you live MSR-quality readings; leaving it on falls back gracefully.

If something still looks off after checking those, [open an issue](https://github.com/Allain-afk/PC_Monitoring/issues/new) and attach both log files plus your motherboard / CPU / GPU model — that's usually enough to add the right vendor poller in a follow-up.

## Tech stack

- **Frontend** — React 19, Vite 8, Tailwind CSS 4, Tauri 2
- **Backend (broadcaster)** — Python 3.11, `psutil`, `pynvml`, `websockets`, packaged with PyInstaller
- **Backend (sensors)** — .NET 8, LibreHardwareMonitorLib 0.9.6, `System.Management` (WMI), `System.Diagnostics.PerformanceCounter`

## Credits

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — the heavy lifting for MSR / SMU / Super-I/O reads
- [Tauri](https://tauri.app/) — the lightweight desktop shell
- [pynvml](https://pypi.org/project/pynvml/) — NVIDIA Management Library bindings

## License

Not yet set. Treat as "all rights reserved" until I add a `LICENSE` file.
