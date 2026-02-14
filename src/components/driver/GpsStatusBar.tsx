import type { DriverCoords } from '@/hooks/use-driver-state';
import type { GpsQuality } from '@/hooks/use-gps-tracking';

interface GpsStatusBarProps {
  coords: DriverCoords | null;
  quality: GpsQuality;
  error: string | null;
  mqttStatus: string;
}

const qualityDot: Record<GpsQuality, string> = {
  high: 'bg-green-500 shadow-[0_0_8px_rgba(34,197,94,0.7)]',
  medium: 'bg-cyan-500 shadow-[0_0_8px_rgba(6,182,212,0.7)]',
  poor: 'bg-yellow-500 shadow-[0_0_8px_rgba(234,179,8,0.7)]',
  none: 'bg-red-500 shadow-[0_0_8px_rgba(239,68,68,0.7)]',
};

export function GpsStatusBar({ coords, quality, error, mqttStatus }: GpsStatusBarProps) {
  let text = '';
  if (error) text = error;
  else if (coords) text = `GPS: ${coords.lat.toFixed(5)}, ${coords.lng.toFixed(5)} (±${Math.round(coords.accuracy)}m)`;
  else text = 'Searching for GPS...';

  const mqttIcon = mqttStatus === 'connected' ? '✅' : mqttStatus === 'connecting' ? '🔄' : '❌';

  return (
    <div className="absolute bottom-20 left-1/2 -translate-x-1/2 z-[1000] bg-white/95 backdrop-blur-sm px-4 py-2 rounded-full shadow-md border border-black/5 flex items-center gap-2 max-w-[90%] text-sm font-semibold text-gray-800">
      <span className={`w-3 h-3 rounded-full flex-shrink-0 ${qualityDot[quality]}`} />
      <span className="truncate">{text}</span>
      <span className="ml-2 text-xs opacity-70">{mqttIcon}</span>
    </div>
  );
}
