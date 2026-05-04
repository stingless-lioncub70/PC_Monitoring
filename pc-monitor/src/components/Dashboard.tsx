import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { CircularGauge } from "./CircularGauge";
import { useHardwareMonitor } from "../hooks/useHardwareMonitor";
import type { ConnectionStatus } from "../types/telemetry";

const TEMP_THRESHOLDS = { warn: 75, critical: 88 };
const PERCENT_THRESHOLDS = { warn: 70, critical: 88 };
const OVERLAY_STORAGE_KEY = "pc-monitor-overlay-enabled";

const TEMP_SOURCE_LABELS: Record<string, string> = {
  lhm: "live (MSR)",
  lenovo: "Lenovo EC",
  perfcounter: "thermal zone",
  acpi: "ACPI (cached)",
  smbios: "SMBIOS probe",
  psutil: "psutil",
};

function StatusDot({ status }: { status: ConnectionStatus }) {
  const style = {
    open: "bg-accent-green shadow-[0_0_8px_#10b981]",
    connecting: "bg-accent-amber animate-pulse",
    closed: "bg-slate-500",
    error: "bg-accent-red",
  }[status];
  return <span className={`inline-block w-2 h-2 rounded-full ${style}`} />;
}

export function Dashboard() {
  const { data, status, error } = useHardwareMonitor("ws://localhost:8765");

  const cpu = data?.cpu;
  const mem = data?.memory;
  const gpu = data?.gpu;
  const disks = data?.disks ?? [];
  const sys = data?.systemInfo;

  const [appVersion, setAppVersion] = useState<string>("");
  useEffect(() => {
    let alive = true;
    import("@tauri-apps/api/app")
      .then(m => m.getVersion())
      .then(v => { if (alive) setAppVersion(v); })
      .catch(() => { if (alive) setAppVersion("dev"); });
    return () => { alive = false; };
  }, []);

  const [overlayEnabled, setOverlayEnabled] = useState<boolean>(
    () => localStorage.getItem(OVERLAY_STORAGE_KEY) === "true",
  );
  const [overlayEditing, setOverlayEditing] = useState<boolean>(false);

  useEffect(() => {
    invoke("set_overlay_visible", { visible: overlayEnabled }).catch(() => {});
    // run once on mount to restore persisted state
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const toggleOverlay = () => {
    const next = !overlayEnabled;
    setOverlayEnabled(next);
    localStorage.setItem(OVERLAY_STORAGE_KEY, String(next));
    invoke("set_overlay_visible", { visible: next }).catch(() => {});
    if (!next && overlayEditing) {
      // hiding the overlay also exits edit mode
      setOverlayEditing(false);
      invoke("set_overlay_interactive", { interactive: false }).catch(() => {});
    }
  };

  const toggleOverlayEditing = () => {
    if (!overlayEnabled) return;
    const next = !overlayEditing;
    setOverlayEditing(next);
    invoke("set_overlay_interactive", { interactive: next }).catch(() => {});
  };

  const headerLine = sys
    ? [sys.cpu, sys.gpu, sys.storage].filter(Boolean).join(" · ")
    : "Detecting hardware…";

  // iGPU: dedicated VRAM is essentially zero, only shared system memory matters.
  const isIgpu = gpu?.integrated === true;
  const memoryGaugeLabel = isIgpu ? "GPU Mem" : "VRAM";

  return (
    <div className="min-h-screen px-8 py-10">
      <header className="flex items-center justify-between mb-10">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight text-white">
            PC Monitor
          </h1>
          <p className="text-sm text-slate-400">{headerLine}</p>
        </div>
        <div className="flex items-center gap-3 text-xs text-slate-400">
          <button
            type="button"
            onClick={toggleOverlay}
            aria-pressed={overlayEnabled}
            title={overlayEnabled ? "Hide overlay" : "Show overlay"}
            className={`font-mono uppercase tracking-wider rounded border px-2 py-1 transition-colors ${
              overlayEnabled
                ? "border-accent-cyan/60 text-accent-cyan bg-accent-cyan/10"
                : "border-slate-700 text-slate-400 hover:text-slate-200 hover:border-slate-500"
            }`}
          >
            Overlay {overlayEnabled ? "On" : "Off"}
          </button>
          <button
            type="button"
            onClick={toggleOverlayEditing}
            disabled={!overlayEnabled}
            aria-pressed={overlayEditing}
            title={
              !overlayEnabled
                ? "Turn the overlay on first"
                : overlayEditing
                  ? "Lock overlay (HUD mode)"
                  : "Drag the overlay or right-click for presets"
            }
            className={`font-mono uppercase tracking-wider rounded border px-2 py-1 transition-colors ${
              !overlayEnabled
                ? "border-slate-800 text-slate-600 cursor-not-allowed"
                : overlayEditing
                  ? "border-accent-amber/60 text-accent-amber bg-accent-amber/10"
                  : "border-slate-700 text-slate-400 hover:text-slate-200 hover:border-slate-500"
            }`}
          >
            {overlayEditing ? "Editing…" : "Edit Position"}
          </button>
          <div className="flex items-center gap-2">
            <StatusDot status={status} />
            <span className="font-mono uppercase tracking-wider">{status}</span>
          </div>
        </div>
      </header>

      {error && (
        <div className="mb-6 rounded-lg border border-accent-red/30 bg-accent-red/10 px-4 py-3 text-sm text-accent-red">
          {error}
        </div>
      )}

      <section className="mb-8">
        <h2 className="text-xs uppercase tracking-[0.2em] text-slate-500 mb-4">
          Compute
        </h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-5">
          <CircularGauge
            label="CPU Load"
            unit="%"
            value={cpu?.utilization ?? 0}
            sublabel={
              cpu?.frequencyMhz ? `${(cpu.frequencyMhz / 1000).toFixed(2)} GHz` : "—"
            }
            thresholds={PERCENT_THRESHOLDS}
          />
          <CircularGauge
            label="CPU Temp"
            unit="°C"
            value={cpu?.temperature ?? 0}
            max={100}
            sublabel={
              cpu?.temperature == null
                ? "run as admin"
                : cpu.powerWatts != null
                  ? `${cpu.powerWatts.toFixed(1)} W`
                  : (cpu.temperatureSource
                      ? (TEMP_SOURCE_LABELS[cpu.temperatureSource] ?? cpu.temperatureSource)
                      : "—")
            }
            thresholds={TEMP_THRESHOLDS}
          />
          <CircularGauge
            label="GPU Load"
            unit="%"
            value={gpu?.utilization ?? 0}
            sublabel={gpu?.name ?? "—"}
            thresholds={PERCENT_THRESHOLDS}
          />
          <CircularGauge
            label="GPU Temp"
            unit="°C"
            value={gpu?.temperature ?? 0}
            max={100}
            sublabel={
              gpu?.temperature == null && isIgpu
                ? "shared w/ CPU"
                : gpu?.powerWatts != null
                  ? `${gpu.powerWatts.toFixed(1)} W`
                  : undefined
            }
            thresholds={TEMP_THRESHOLDS}
          />
        </div>
      </section>

      <section>
        <h2 className="text-xs uppercase tracking-[0.2em] text-slate-500 mb-4">
          Memory & Storage
        </h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-5">
          <CircularGauge
            label="RAM"
            unit="%"
            value={mem?.percent ?? 0}
            sublabel={mem ? `${mem.usedGb} / ${mem.totalGb} GB` : undefined}
            thresholds={PERCENT_THRESHOLDS}
          />
          <CircularGauge
            label={memoryGaugeLabel}
            unit={gpu?.memoryUtilization != null ? "%" : "GB"}
            value={
              gpu?.memoryUtilization != null
                ? gpu.memoryUtilization
                : gpu?.memoryUsedMb != null
                  ? Number((gpu.memoryUsedMb / 1024).toFixed(1))
                  : 0
            }
            max={
              gpu?.memoryUtilization != null
                ? 100
                : (mem?.totalGb ?? 16)
            }
            sublabel={
              gpu?.memoryUsedMb != null && gpu.memoryTotalMb != null
                ? `${(gpu.memoryUsedMb / 1024).toFixed(1)} / ${(gpu.memoryTotalMb / 1024).toFixed(1)} GB`
                : gpu?.memoryUsedMb != null
                  ? `${(gpu.memoryUsedMb / 1024).toFixed(1)} GB shared`
                  : undefined
            }
            thresholds={PERCENT_THRESHOLDS}
          />
          {disks.map(d => (
            <CircularGauge
              key={d.diskNumber}
              label={`Disk ${d.diskNumber}${d.driveLetters.length ? ` (${d.driveLetters.join(", ")})` : ""}`}
              unit="%"
              value={d.percent}
              sublabel={`${d.usedGb} / ${d.totalGb} GB · ${d.type}`}
              footer={
                d.readMb != null && d.writeMb != null
                  ? `R ${(d.readMb / 1024).toFixed(1)} GB · W ${(d.writeMb / 1024).toFixed(1)} GB`
                  : "R — · W —"
              }
              thresholds={{ warn: 80, critical: 92 }}
            />
          ))}
        </div>
      </section>

      <footer className="mt-10 text-[11px] text-slate-600 font-mono flex items-center gap-3">
        <span>
          last update:{" "}
          {data?.timestamp
            ? new Date(data.timestamp * 1000).toLocaleTimeString()
            : "—"}
        </span>
        <span className="text-slate-700">·</span>
        <span>v{appVersion || "…"}</span>
      </footer>
    </div>
  );
}
