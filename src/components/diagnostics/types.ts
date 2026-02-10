/** Reusable diagnostic panel types */

export interface DiagnosticParam {
  key: string;
  label: string;
  description: string;
  type: "slider" | "toggle" | "select";
  min?: number;
  max?: number;
  step?: number;
  unit?: string;
  options?: { label: string; value: string }[];
  defaultValue: number | boolean | string;
}

export interface DiagnosticGroup {
  id: string;
  label: string;
  icon: string;
  description: string;
  params: DiagnosticParam[];
}

export interface MeterReading {
  label: string;
  value: number;
  peak: number;
  min: number;
  threshold?: number;
  unit?: string;
  status: "silent" | "low" | "good" | "hot" | "clipping";
}

export interface DiagnosticSnapshot {
  timestamp: number;
  meters: Record<string, MeterReading>;
  params: Record<string, number | boolean | string>;
}
