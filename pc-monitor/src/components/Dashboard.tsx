import { CircularGauge } from "./CircularGauge";
import { useHardwareMonitor } from "../hooks/useHardwareMonitor";
import type { ConnectionStatus } from "../types/telemetry";

const TEMP_THRESHOLDS = { warn: 75, critical: 88 };
const PERCENT_THRESHOLDS = { warn: 70, critical: 88 };

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
  const disk = data?.disk;

  return (
    <div className="min-h-screen px-8 py-10">
      <header className="flex items-center justify-between mb-10">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight text-white">
            PC Monitor
          </h1>
          <p className="text-sm text-slate-400">
            Ryzen 7 7435HS · RTX 4050 Laptop · NVMe
          </p>
        </div>
        <div className="flex items-center gap-2 text-xs text-slate-400">
          <StatusDot status={status} />
          <span className="font-mono uppercase tracking-wider">{status}</span>
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
                  : "k10temp"
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
              gpu?.powerWatts != null ? `${gpu.powerWatts.toFixed(1)} W` : undefined
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
            label="VRAM"
            unit="%"
            value={gpu?.memoryUtilization ?? 0}
            sublabel={
              gpu?.memoryUsedMb != null && gpu.memoryTotalMb != null
                ? `${(gpu.memoryUsedMb / 1024).toFixed(1)} / ${(gpu.memoryTotalMb / 1024).toFixed(1)} GB`
                : undefined
            }
            thresholds={PERCENT_THRESHOLDS}
          />
          <CircularGauge
            label="Disk Used"
            unit="%"
            value={disk?.percent ?? 0}
            sublabel={disk ? `${disk.usedGb} / ${disk.totalGb} GB` : undefined}
            thresholds={{ warn: 80, critical: 92 }}
          />
          <div className="rounded-2xl bg-bg-900/70 border border-white/5 p-5 flex flex-col justify-center">
            <div className="text-xs uppercase tracking-[0.18em] text-slate-400 mb-3">
              Disk I/O & Fans
            </div>
            <div className="font-mono text-sm text-slate-200 space-y-1">
              <div>
                Read:{" "}
                <span className="text-accent-cyan">
                  {disk?.readMb?.toLocaleString() ?? "—"} MB
                </span>
              </div>
              <div>
                Write:{" "}
                <span className="text-accent-green">
                  {disk?.writeMb?.toLocaleString() ?? "—"} MB
                </span>
              </div>
              <div>
                Fan:{" "}
                <span className="text-accent-amber">
                  {data?.fans?.rpm != null
                    ? `${data.fans.rpm.toLocaleString()} RPM`
                    : "—"}
                </span>
              </div>
            </div>
          </div>
        </div>
      </section>

      <footer className="mt-10 text-[11px] text-slate-600 font-mono">
        last update:{" "}
        {data?.timestamp
          ? new Date(data.timestamp * 1000).toLocaleTimeString()
          : "—"}
      </footer>
    </div>
  );
}
