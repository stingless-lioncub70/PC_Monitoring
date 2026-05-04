export interface CpuTelemetry {
  utilization: number;
  perCore: number[];
  frequencyMhz: number | null;
  temperature: number | null;
  temperatureSource: string | null;
  powerWatts: number | null;
}

export interface FanTelemetry {
  rpm: number | null;
}

export interface MemoryTelemetry {
  percent: number;
  usedGb: number;
  totalGb: number;
}

export interface GpuTelemetry {
  available: boolean;
  name?: string;
  utilization?: number | null;
  memoryUtilization?: number | null;
  memoryUsedMb?: number | null;
  memoryTotalMb?: number | null;
  temperature?: number | null;
  powerWatts?: number | null;
  source?: "nvml" | "wddm";
  integrated?: boolean;
  error?: string;
}

export interface SystemInfo {
  cpu: string;
  gpu: string | null;
  storage: string;
}

export interface DiskTelemetry {
  percent: number;
  usedGb: number;
  totalGb: number;
  readMb: number | null;
  writeMb: number | null;
}

export interface Telemetry {
  timestamp: number;
  systemInfo?: SystemInfo;
  cpu: CpuTelemetry;
  memory: MemoryTelemetry;
  gpu: GpuTelemetry;
  disk: DiskTelemetry;
  fans?: FanTelemetry;
}

export type ConnectionStatus = "connecting" | "open" | "closed" | "error";
