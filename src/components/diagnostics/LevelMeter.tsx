import { cn } from "@/lib/utils";
import type { MeterReading } from "./types";

interface LevelMeterProps {
  reading: MeterReading;
  className?: string;
}

const statusColors: Record<MeterReading["status"], string> = {
  silent: "bg-muted-foreground/30",
  low: "bg-blue-500",
  good: "bg-emerald-500",
  hot: "bg-amber-500",
  clipping: "bg-destructive",
};

const statusLabels: Record<MeterReading["status"], string> = {
  silent: "SILENT",
  low: "LOW",
  good: "GOOD",
  hot: "HOT",
  clipping: "CLIP",
};

export function LevelMeter({ reading, className }: LevelMeterProps) {
  const maxScale = reading.threshold ? Math.max(reading.peak, reading.threshold) * 1.3 : reading.peak * 1.3 || 1;
  const valuePct = Math.min((reading.value / maxScale) * 100, 100);
  const peakPct = Math.min((reading.peak / maxScale) * 100, 100);
  const thresholdPct = reading.threshold ? Math.min((reading.threshold / maxScale) * 100, 100) : null;

  return (
    <div className={cn("space-y-1.5", className)}>
      <div className="flex items-center justify-between text-xs">
        <span className="text-muted-foreground font-medium">{reading.label}</span>
        <div className="flex items-center gap-2">
          <span className="font-mono text-foreground/80">
            {Math.round(reading.value)}
            {reading.unit ? ` ${reading.unit}` : ""}
          </span>
          <span
            className={cn(
              "px-1.5 py-0.5 rounded text-[10px] font-bold uppercase tracking-wider",
              reading.status === "good" && "bg-emerald-500/20 text-emerald-400",
              reading.status === "hot" && "bg-amber-500/20 text-amber-400",
              reading.status === "clipping" && "bg-destructive/20 text-destructive",
              reading.status === "low" && "bg-blue-500/20 text-blue-400",
              reading.status === "silent" && "bg-muted text-muted-foreground"
            )}
          >
            {statusLabels[reading.status]}
          </span>
        </div>
      </div>

      {/* Meter bar */}
      <div className="relative h-3 rounded-full bg-secondary overflow-hidden">
        {/* Value fill */}
        <div
          className={cn("absolute inset-y-0 left-0 rounded-full transition-all duration-150", statusColors[reading.status])}
          style={{ width: `${valuePct}%` }}
        />

        {/* Peak marker */}
        <div
          className="absolute inset-y-0 w-0.5 bg-foreground/60 transition-all duration-300"
          style={{ left: `${peakPct}%` }}
        />

        {/* Threshold line */}
        {thresholdPct !== null && (
          <div
            className="absolute inset-y-0 w-0.5 border-l border-dashed border-primary/70"
            style={{ left: `${thresholdPct}%` }}
            title={`Threshold: ${reading.threshold}`}
          />
        )}
      </div>

      {/* Scale labels */}
      <div className="flex justify-between text-[10px] text-muted-foreground/50">
        <span>0</span>
        {reading.threshold && (
          <span className="text-primary/60">⚡ {reading.threshold}</span>
        )}
        <span>{Math.round(maxScale)}</span>
      </div>
    </div>
  );
}
