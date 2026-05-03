export interface CpuTelemetry {
  utilization: number;
  perCore: number[];
  frequencyMhz: number | null;
  temperature: number | null;
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
  utilization?: number;
  memoryUtilization?: number;
  memoryUsedMb?: number;
  memoryTotalMb?: number;
  temperature?: number;
  powerWatts?: number | null;
  error?: string;
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
  cpu: CpuTelemetry;
  memory: MemoryTelemetry;
  gpu: GpuTelemetry;
  disk: DiskTelemetry;
  fans?: FanTelemetry;
}

export type ConnectionStatus = "connecting" | "open" | "closed" | "error";
