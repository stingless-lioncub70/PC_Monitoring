import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { useHardwareMonitor } from "../hooks/useHardwareMonitor";

const DASH = "--";

function num(v: number | null | undefined, digits = 0): string {
  return v == null || Number.isNaN(v) ? DASH : v.toFixed(digits);
}

type Preset = "above-tray" | "top-left" | "top-right" | "bottom-left" | "bottom-right";

const PRESETS: { id: Preset; label: string }[] = [
  { id: "above-tray", label: "Above Tray" },
  { id: "top-left", label: "Top Left" },
  { id: "top-right", label: "Top Right" },
  { id: "bottom-left", label: "Bottom Left" },
  { id: "bottom-right", label: "Bottom Right" },
];

export function Overlay() {
  const { data } = useHardwareMonitor("ws://localhost:8765");
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);

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

  // Dismiss the context menu when clicking elsewhere or pressing Escape.
  useEffect(() => {
    if (!menu) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setMenu(null); };
    const onClick = () => setMenu(null);
    window.addEventListener("keydown", onKey);
    window.addEventListener("click", onClick);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("click", onClick);
    };
  }, [menu]);

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

  const handleContextMenu = (e: React.MouseEvent) => {
    e.preventDefault();
    setMenu({ x: e.clientX, y: e.clientY });
  };

  const applyPreset = async (preset: Preset) => {
    setMenu(null);
    try { await invoke("move_overlay_to_preset", { preset }); } catch { /* no-op */ }
  };

  const stopEditing = async () => {
    setMenu(null);
    try { await invoke("set_overlay_interactive", { interactive: false }); } catch { /* no-op */ }
  };

  return (
    <div
      data-tauri-drag-region
      onContextMenu={handleContextMenu}
      className="font-mono text-[13px] leading-tight text-white px-3 py-2 select-none cursor-move"
      style={lineStyle}
    >
      <div data-tauri-drag-region>
        <span className="text-accent-cyan">CPU </span>
        {num(cpu?.utilization, 0)}%  {num(cpu?.temperature, 0)}°C
        {cpu?.powerWatts != null && `  ${cpu.powerWatts.toFixed(1)}W`}
      </div>
      <div data-tauri-drag-region>
        <span className="text-accent-cyan">GPU </span>
        {gpuOk ? num(gpu?.utilization, 0) : DASH}%  {gpuOk ? num(gpu?.temperature, 0) : DASH}°C
        {gpuOk && gpu?.powerWatts != null && `  ${gpu.powerWatts.toFixed(1)}W`}
      </div>
      <div data-tauri-drag-region>
        <span className="text-accent-cyan">RAM </span>
        {num(mem?.percent, 0)}%  {num(mem?.usedGb, 1)}/{num(mem?.totalGb, 1)} GB
      </div>
      <div data-tauri-drag-region>
        <span className="text-accent-cyan">VRAM</span>{" "}
        {num(vramPct, 0)}%  {gpuOk ? num(gpu?.memoryUsedMb, 0) : DASH}/
        {gpuOk ? num(gpu?.memoryTotalMb, 0) : DASH} MB
      </div>

      {menu && (
        <div
          onClick={(e) => e.stopPropagation()}
          onContextMenu={(e) => e.preventDefault()}
          className="absolute z-50 min-w-[140px] rounded-md border border-white/10 bg-black/85 backdrop-blur shadow-lg py-1 text-[12px]"
          style={{ left: menu.x, top: menu.y, textShadow: "none" }}
        >
          {PRESETS.map((p) => (
            <button
              key={p.id}
              onClick={() => applyPreset(p.id)}
              className="block w-full text-left px-3 py-1 hover:bg-white/10 text-slate-200"
            >
              {p.label}
            </button>
          ))}
          <div className="my-1 border-t border-white/10" />
          <button
            onClick={stopEditing}
            className="block w-full text-left px-3 py-1 hover:bg-white/10 text-accent-cyan"
          >
            Done editing
          </button>
        </div>
      )}
    </div>
  );
}
