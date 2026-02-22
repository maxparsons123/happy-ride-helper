import { Car, Users, Briefcase } from "lucide-react";
import { cn } from "@/lib/utils";
import type { VehicleOption } from "@/pages/AirportBooking";

interface VehicleCardProps {
  vehicle: VehicleOption;
  selected: boolean;
  onSelect: () => void;
}

export function VehicleCard({ vehicle, selected, onSelect }: VehicleCardProps) {
  return (
    <button
      onClick={onSelect}
      className={cn(
        "relative flex flex-col items-center text-center rounded-xl border-2 p-4 transition-all duration-200 hover:shadow-lg",
        selected
          ? "border-primary bg-primary/10 shadow-glow"
          : "border-border bg-card hover:border-muted-foreground/30"
      )}
    >
      {vehicle.tier === "executive" && (
        <span className="absolute top-2 right-2 text-[10px] font-bold uppercase tracking-wider bg-primary/20 text-primary px-2 py-0.5 rounded-full">
          Premium
        </span>
      )}

      <div className="h-16 w-full flex items-center justify-center mb-3">
        <Car className={cn("h-10 w-10", selected ? "text-primary" : "text-muted-foreground")} />
      </div>

      <h3 className="font-semibold text-sm text-foreground mb-1">{vehicle.name}</h3>

      <div className="flex items-center gap-3 text-xs text-muted-foreground mb-2">
        <span className="flex items-center gap-1">
          <Users className="h-3 w-3" />
          {vehicle.maxPassengers}
        </span>
        <span className="flex items-center gap-1">
          <Briefcase className="h-3 w-3" />
          {vehicle.maxSuitcases}
        </span>
      </div>

      <p className="text-[11px] text-muted-foreground leading-tight mb-3">{vehicle.description}</p>

      {vehicle.fare && (
        <div className="mt-auto">
          <span className="text-lg font-bold text-primary">£{vehicle.fare}</span>
        </div>
      )}
    </button>
  );
}
