import { MapPin, Navigation, Users, CheckCircle2 } from "lucide-react";
import { cn } from "@/lib/utils";

interface BookingStatusProps {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  status: "collecting" | "confirmed" | "info_only";
}

export function BookingStatus({
  pickup,
  destination,
  passengers,
  status,
}: BookingStatusProps) {
  const isConfirmed = status === "confirmed";
  const items = [
    { label: "Pickup", value: pickup, icon: MapPin },
    { label: "Destination", value: destination, icon: Navigation },
    { label: "Passengers", value: passengers ? String(passengers) : null, icon: Users },
  ];

  const completedCount = items.filter((item) => item.value).length;
  const progress = (completedCount / 3) * 100;

  return (
    <div className="rounded-xl border border-chat-border bg-card p-4 space-y-4">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-display font-semibold text-foreground">
          Booking Details
        </h3>
        {isConfirmed ? (
          <span className="flex items-center gap-1.5 text-xs font-medium text-success">
            <CheckCircle2 className="h-3.5 w-3.5" />
            Confirmed
          </span>
        ) : (
          <span className="text-xs text-muted-foreground">
            {completedCount}/3 collected
          </span>
        )}
      </div>

      {!isConfirmed && (
        <div className="h-1.5 rounded-full bg-muted overflow-hidden">
          <div
            className="h-full bg-gradient-gold transition-all duration-500 ease-out"
            style={{ width: `${progress}%` }}
          />
        </div>
      )}

      <div className="space-y-2">
        {items.map(({ label, value, icon: Icon }) => (
          <div
            key={label}
            className={cn(
              "flex items-center gap-3 rounded-lg p-2.5 transition-all duration-300",
              value
                ? "bg-secondary/50 border border-primary/20"
                : "bg-muted/30 border border-transparent"
            )}
          >
            <div
              className={cn(
                "flex h-7 w-7 items-center justify-center rounded-md transition-colors",
                value ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground"
              )}
            >
              <Icon className="h-3.5 w-3.5" />
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-xs text-muted-foreground">{label}</p>
              <p
                className={cn(
                  "text-sm font-medium truncate",
                  value ? "text-foreground" : "text-muted-foreground/50"
                )}
              >
                {value || "Waiting..."}
              </p>
            </div>
            {value && (
              <CheckCircle2 className="h-4 w-4 text-primary shrink-0" />
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
