import { Slider } from "@/components/ui/slider";
import { Switch } from "@/components/ui/switch";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { HelpCircle } from "lucide-react";
import type { DiagnosticParam } from "./types";

interface ParamControlProps {
  param: DiagnosticParam;
  value: number | boolean | string;
  onChange: (key: string, value: number | boolean | string) => void;
}

export function ParamControl({ param, value, onChange }: ParamControlProps) {
  return (
    <div className="space-y-2 py-3 border-b border-border/40 last:border-0">
      {/* Label row */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-1.5">
          <span className="text-sm font-medium text-foreground">{param.label}</span>
          <Tooltip>
            <TooltipTrigger asChild>
              <HelpCircle className="h-3.5 w-3.5 text-muted-foreground/60 cursor-help" />
            </TooltipTrigger>
            <TooltipContent side="right" className="max-w-xs text-xs leading-relaxed">
              {param.description}
            </TooltipContent>
          </Tooltip>
        </div>

        {/* Value display */}
        {param.type === "slider" && (
          <span className="text-sm font-mono text-primary tabular-nums">
            {typeof value === "number" ? (Number.isInteger(param.step) ? value : (value as number).toFixed(1)) : value}
            {param.unit ? ` ${param.unit}` : ""}
          </span>
        )}
      </div>

      {/* Control */}
      {param.type === "slider" && (
        <Slider
          min={param.min}
          max={param.max}
          step={param.step}
          value={[value as number]}
          onValueChange={([v]) => onChange(param.key, v)}
          className="py-1"
        />
      )}

      {param.type === "toggle" && (
        <Switch
          checked={value as boolean}
          onCheckedChange={(v) => onChange(param.key, v)}
        />
      )}

      {param.type === "select" && param.options && (
        <Select value={value as string} onValueChange={(v) => onChange(param.key, v)}>
          <SelectTrigger className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {param.options.map((opt) => (
              <SelectItem key={opt.value} value={opt.value}>
                {opt.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
    </div>
  );
}
