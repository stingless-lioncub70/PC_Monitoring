import { useEffect } from "react";
import { useHardwareMonitor } from "../hooks/useHardwareMonitor";

const DASH = "--";

function num(v: number | null | undefined, digits = 0): string {
  return v == null || Number.isNaN(v) ? DASH : v.toFixed(digits);
}

export function Overlay() {
  const { data } = useHardwareMonitor("ws://localhost:8765");

  useEffect(() => {
    const prevHtml = document.documentElement.style.background;
    const prevBody = document.body.style.background;
    document.documentElement.style.background = "transparent";
    document.body.style.background = "transparent";
    document.body.style.backgroundImage = "none";
    return () => {
      document.documentElement.style.background = prevHtml;
      document.body.style.background = prevBody;
    };
  }, []);

  const cpu = data?.cpu;
  const gpu = data?.gpu;
  const mem = data?.memory;

  const gpuOk = gpu?.available === true;
  const vramPct =
    gpuOk && gpu?.memoryUtilization != null
      ? gpu.memoryUtilization
      : gpuOk && gpu?.memoryUsedMb != null && gpu?.memoryTotalMb
        ? (gpu.memoryUsedMb / gpu.memoryTotalMb) * 100
        : null;

  const lineStyle: React.CSSProperties = {
    textShadow:
      "0 0 2px #000, 0 0 4px #000, 1px 1px 2px rgba(0,0,0,0.9), -1px -1px 2px rgba(0,0,0,0.9)",
  };

  return (
    <div
      className="font-mono text-[13px] leading-tight text-white px-3 py-2 select-none"
      style={lineStyle}
    >
      <div>
        <span className="text-accent-cyan">CPU </span>
        {num(cpu?.utilization, 0)}%  {num(cpu?.temperature, 0)}°C
        {cpu?.powerWatts != null && `  ${cpu.powerWatts.toFixed(1)}W`}
      </div>
      <div>
        <span className="text-accent-cyan">GPU </span>
        {gpuOk ? num(gpu?.utilization, 0) : DASH}%  {gpuOk ? num(gpu?.temperature, 0) : DASH}°C
        {gpuOk && gpu?.powerWatts != null && `  ${gpu.powerWatts.toFixed(1)}W`}
      </div>
      <div>
        <span className="text-accent-cyan">RAM </span>
        {num(mem?.percent, 0)}%  {num(mem?.usedGb, 1)}/{num(mem?.totalGb, 1)} GB
      </div>
      <div>
        <span className="text-accent-cyan">VRAM</span>{" "}
        {num(vramPct, 0)}%  {gpuOk ? num(gpu?.memoryUsedMb, 0) : DASH}/
        {gpuOk ? num(gpu?.memoryTotalMb, 0) : DASH} MB
      </div>
    </div>
  );
}
