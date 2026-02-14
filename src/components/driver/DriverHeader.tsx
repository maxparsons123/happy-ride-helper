import { Menu } from 'lucide-react';
import type { DriverPresence } from '@/hooks/use-driver-state';

interface DriverHeaderProps {
  presence: DriverPresence;
  onMenuToggle: () => void;
}

const presenceConfig = {
  available: { label: 'Available', dotClass: 'bg-green-500 shadow-[0_0_8px_rgba(34,197,94,0.7)]' },
  busy: { label: 'On Job', dotClass: 'bg-yellow-500 shadow-[0_0_8px_rgba(234,179,8,0.7)]' },
  offline: { label: 'Offline', dotClass: 'bg-red-500 shadow-[0_0_8px_rgba(239,68,68,0.7)]' },
};

export function DriverHeader({ presence, onMenuToggle }: DriverHeaderProps) {
  const p = presenceConfig[presence];

  return (
    <header className="absolute top-0 left-0 right-0 z-[1001] flex items-center justify-between px-4 py-3 bg-gradient-to-r from-black to-[#1a1a1a] border-b-2 border-[#FFD700] shadow-lg">
      <div className="flex flex-col">
        <h1 className="text-[#FFD700] font-extrabold text-lg flex items-center gap-2">
          <span className="text-xl drop-shadow-md">🚕</span>
          Black Cab Unite
        </h1>
        <div className="flex items-center gap-1.5 text-[#FFD700]/80 text-xs mt-0.5">
          <span className={`w-2 h-2 rounded-full ${p.dotClass}`} />
          {p.label}
        </div>
      </div>
      <button
        onClick={onMenuToggle}
        className="w-11 h-11 rounded-full bg-gradient-to-br from-[#FFD700] to-[#FFC107] text-black flex items-center justify-center font-extrabold shadow-lg active:scale-[0.92] transition-transform"
      >
        <Menu size={20} />
      </button>
    </header>
  );
}
