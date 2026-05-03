import { useId, useMemo } from "react";

export type Threshold = { warn: number; critical: number };

export interface CircularGaugeProps {
  value: number;
  max?: number;
  label: string;
  unit: string;
  sublabel?: string;
  thresholds?: Threshold;
  size?: number;
  strokeWidth?: number;
}

const SIZE_DEFAULT = 180;
const STROKE_DEFAULT = 12;

function pickColor(
  pct: number,
  t: Threshold,
): { stroke: string; glow: string; text: string } {
  if (pct >= t.critical)
    return { stroke: "#ef4444", glow: "rgba(239,68,68,0.45)", text: "text-accent-red" };
  if (pct >= t.warn)
    return { stroke: "#f59e0b", glow: "rgba(245,158,11,0.40)", text: "text-accent-amber" };
  return { stroke: "#22d3ee", glow: "rgba(34,211,238,0.40)", text: "text-accent-cyan" };
}

export function CircularGauge({
  value,
  max = 100,
  label,
  unit,
  sublabel,
  thresholds = { warn: 70, critical: 88 },
  size = SIZE_DEFAULT,
  strokeWidth = STROKE_DEFAULT,
}: CircularGaugeProps) {
  const gradId = useId();
  const safeValue = Number.isFinite(value) ? value : 0;
  const pct = Math.max(0, Math.min(100, (safeValue / max) * 100));
  const color = useMemo(() => pickColor(pct, thresholds), [pct, thresholds]);

  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const dashOffset = circumference * (1 - pct / 100);

  return (
    <div
      className="flex flex-col items-center justify-center rounded-2xl bg-bg-900/70 border border-white/5 p-5 shadow-[0_0_0_1px_rgba(255,255,255,0.02)] backdrop-blur"
      style={{ minWidth: size + 32 }}
    >
      <div className="relative" style={{ width: size, height: size }}>
        <svg width={size} height={size} className="-rotate-90">
          <defs>
            <linearGradient id={gradId} x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stopColor={color.stroke} stopOpacity="1" />
              <stop offset="100%" stopColor={color.stroke} stopOpacity="0.55" />
            </linearGradient>
          </defs>

          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            stroke="rgba(255,255,255,0.06)"
            strokeWidth={strokeWidth}
            fill="none"
          />

          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            stroke={`url(#${gradId})`}
            strokeWidth={strokeWidth}
            strokeLinecap="round"
            fill="none"
            strokeDasharray={circumference}
            strokeDashoffset={dashOffset}
            style={{
              transition:
                "stroke-dashoffset 700ms cubic-bezier(0.22,1,0.36,1), stroke 300ms",
              filter: `drop-shadow(0 0 6px ${color.glow})`,
            }}
          />
        </svg>

        <div className="absolute inset-0 flex flex-col items-center justify-center">
          <span
            className={`font-mono text-3xl font-semibold tabular-nums ${color.text}`}
          >
            {safeValue.toFixed(safeValue >= 100 ? 0 : 1)}
            <span className="text-base text-slate-400 ml-0.5">{unit}</span>
          </span>
          {sublabel && (
            <span className="mt-1 text-[11px] text-slate-500">{sublabel}</span>
          )}
        </div>
      </div>

      <div className="mt-3 text-xs uppercase tracking-[0.18em] text-slate-400">
        {label}
      </div>
    </div>
  );
}
