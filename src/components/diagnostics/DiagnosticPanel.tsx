import { useState, useEffect, useCallback } from "react";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from "@/components/ui/accordion";
import { RotateCcw, Copy, Download } from "lucide-react";
import { toast } from "sonner";
import { LevelMeter } from "./LevelMeter";
import { ParamControl } from "./ParamControl";
import { defaultDiagnosticGroups } from "./presets";
import type { DiagnosticGroup, MeterReading } from "./types";

interface DiagnosticPanelProps {
  /** Override default groups with custom ones */
  groups?: DiagnosticGroup[];
  /** Called when any parameter changes — wire this to your bridge */
  onParamChange?: (key: string, value: number | boolean | string) => void;
  /** External meter readings pushed in from a WebSocket or polling source */
  meterReadings?: Record<string, MeterReading>;
  /** Whether the bridge is currently connected */
  isConnected?: boolean;
  className?: string;
}

export function DiagnosticPanel({
  groups = defaultDiagnosticGroups,
  onParamChange,
  meterReadings: externalMeters,
  isConnected = false,
  className,
}: DiagnosticPanelProps) {
  // Build initial values from defaults
  const buildDefaults = useCallback(() => {
    const defaults: Record<string, number | boolean | string> = {};
    groups.forEach((g) =>
      g.params.forEach((p) => {
        defaults[p.key] = p.defaultValue;
      })
    );
    return defaults;
  }, [groups]);

  const [values, setValues] = useState<Record<string, number | boolean | string>>(buildDefaults);

  // Simulated meters for demo when no external source
  const [demoMeters, setDemoMeters] = useState<Record<string, MeterReading>>({
    ingressRms: { label: "Ingress RMS (Caller)", value: 0, peak: 0, min: 0, threshold: values.bargeInRmsThreshold as number, unit: "RMS", status: "silent" },
    egressRms: { label: "Egress RMS (Ada)", value: 0, peak: 0, min: 0, unit: "RMS", status: "silent" },
    vadLevel: { label: "VAD Confidence", value: 0, peak: 0, min: 0, threshold: (values.vadThreshold as number) * 10000, unit: "", status: "silent" },
  });

  // Simulate meter movement when no external feed
  useEffect(() => {
    if (externalMeters) return;
    const iv = setInterval(() => {
      setDemoMeters((prev) => {
        const jitter = () => Math.random() * 400 - 200;
        const ingressVal = Math.max(0, 800 + jitter() * 3);
        const egressVal = Math.max(0, 1200 + jitter() * 2);
        const vadVal = Math.max(0, Math.min(10000, 2500 + jitter() * 5));
        return {
          ingressRms: {
            ...prev.ingressRms,
            value: ingressVal,
            peak: Math.max(prev.ingressRms.peak * 0.98, ingressVal),
            min: Math.min(prev.ingressRms.min === 0 ? ingressVal : prev.ingressRms.min, ingressVal),
            threshold: values.bargeInRmsThreshold as number,
            status: ingressVal < 300 ? "silent" : ingressVal < 800 ? "low" : ingressVal < 2500 ? "good" : ingressVal < 4000 ? "hot" : "clipping",
          },
          egressRms: {
            ...prev.egressRms,
            value: egressVal,
            peak: Math.max(prev.egressRms.peak * 0.98, egressVal),
            min: Math.min(prev.egressRms.min === 0 ? egressVal : prev.egressRms.min, egressVal),
            status: egressVal < 300 ? "silent" : egressVal < 800 ? "low" : egressVal < 2500 ? "good" : egressVal < 4000 ? "hot" : "clipping",
          },
          vadLevel: {
            ...prev.vadLevel,
            value: vadVal,
            peak: Math.max(prev.vadLevel.peak * 0.98, vadVal),
            min: Math.min(prev.vadLevel.min === 0 ? vadVal : prev.vadLevel.min, vadVal),
            threshold: (values.vadThreshold as number) * 10000,
            status: vadVal < 1000 ? "silent" : vadVal < 2000 ? "low" : vadVal < 5000 ? "good" : vadVal < 8000 ? "hot" : "clipping",
          },
        };
      });
    }, 150);
    return () => clearInterval(iv);
  }, [externalMeters, values.bargeInRmsThreshold, values.vadThreshold]);

  const meters = externalMeters ?? demoMeters;

  const handleChange = (key: string, value: number | boolean | string) => {
    setValues((prev) => ({ ...prev, [key]: value }));
    onParamChange?.(key, value);
  };

  const handleReset = () => {
    const defaults = buildDefaults();
    setValues(defaults);
    Object.entries(defaults).forEach(([k, v]) => onParamChange?.(k, v));
    toast.success("All parameters reset to defaults");
  };

  const handleExport = () => {
    const config = JSON.stringify(values, null, 2);
    navigator.clipboard.writeText(config);
    toast.success("Configuration copied to clipboard");
  };

  const handleDownload = () => {
    const blob = new Blob([JSON.stringify(values, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `diagnostic-config-${Date.now()}.json`;
    a.click();
    URL.revokeObjectURL(url);
    toast.success("Configuration downloaded");
  };

  return (
    <div className={className}>
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <div>
          <h2 className="text-xl font-bold text-foreground font-['Space_Grotesk']">
            🔬 Live Diagnostics
          </h2>
          <p className="text-sm text-muted-foreground mt-1">
            Real-time audio & AI parameters — changes apply instantly to live calls
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Badge variant={isConnected ? "default" : "secondary"} className="text-xs">
            {isConnected ? "● Connected" : "○ Demo Mode"}
          </Badge>
          <Button variant="ghost" size="icon" onClick={handleReset} title="Reset all to defaults">
            <RotateCcw className="h-4 w-4" />
          </Button>
          <Button variant="ghost" size="icon" onClick={handleExport} title="Copy config">
            <Copy className="h-4 w-4" />
          </Button>
          <Button variant="ghost" size="icon" onClick={handleDownload} title="Download config">
            <Download className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Level Meters */}
      <Card className="mb-6 border-border/50">
        <CardHeader className="pb-3">
          <CardTitle className="text-sm font-medium">📊 Audio Levels</CardTitle>
          <CardDescription className="text-xs">
            Dashed line = threshold. Peak marker fades over time.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {Object.values(meters).map((m) => (
            <LevelMeter key={m.label} reading={m} />
          ))}
        </CardContent>
      </Card>

      {/* Parameter Groups */}
      <Accordion type="multiple" defaultValue={groups.map((g) => g.id)} className="space-y-3">
        {groups.map((group) => (
          <AccordionItem key={group.id} value={group.id} className="border border-border/50 rounded-lg overflow-hidden">
            <AccordionTrigger className="px-4 py-3 hover:no-underline hover:bg-secondary/30">
              <div className="flex items-center gap-2 text-left">
                <span className="text-lg">{group.icon}</span>
                <div>
                  <div className="text-sm font-semibold text-foreground">{group.label}</div>
                  <div className="text-xs text-muted-foreground font-normal">{group.description}</div>
                </div>
              </div>
            </AccordionTrigger>
            <AccordionContent className="px-4 pb-2">
              {group.params.map((param) => (
                <ParamControl
                  key={param.key}
                  param={param}
                  value={values[param.key]}
                  onChange={handleChange}
                />
              ))}
            </AccordionContent>
          </AccordionItem>
        ))}
      </Accordion>
    </div>
  );
}
