import { ArrowLeftRight, Calendar, Clock, Plane, Gift, Tag } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";

interface ReturnTripSectionProps {
  wantReturn: boolean;
  setWantReturn: (v: boolean) => void;
  returnDate: string;
  setReturnDate: (v: string) => void;
  returnTime: string;
  setReturnTime: (v: string) => void;
  returnFlightNumber: string;
  setReturnFlightNumber: (v: string) => void;
  discountPct: number;
  selectedVehicleFare?: string;
}

export function ReturnTripSection({
  wantReturn, setWantReturn,
  returnDate, setReturnDate,
  returnTime, setReturnTime,
  returnFlightNumber, setReturnFlightNumber,
  discountPct,
  selectedVehicleFare,
}: ReturnTripSectionProps) {
  const discountedFare = selectedVehicleFare
    ? (parseFloat(selectedVehicleFare) * (1 - discountPct / 100)).toFixed(2)
    : null;

  return (
    <section className="bg-card border border-border rounded-xl p-6 space-y-5">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-foreground flex items-center gap-2">
          <ArrowLeftRight className="h-5 w-5 text-primary" />
          Return Trip
        </h2>
        <Switch checked={wantReturn} onCheckedChange={setWantReturn} />
      </div>

      {/* Promo banner */}
      <div className="bg-primary/10 border border-primary/20 rounded-lg p-4 flex items-start gap-3">
        <Gift className="h-5 w-5 text-primary mt-0.5 flex-shrink-0" />
        <div>
          <p className="text-sm font-semibold text-foreground">
            🎉 {discountPct}% off your return journey!
          </p>
          <p className="text-xs text-muted-foreground mt-1">
            Book your return trip now and save. The same vehicle will be arranged for your return.
          </p>
          {wantReturn && selectedVehicleFare && discountedFare && (
            <div className="flex items-center gap-2 mt-2">
              <Tag className="h-3.5 w-3.5 text-primary" />
              <span className="text-xs text-muted-foreground line-through">£{selectedVehicleFare}</span>
              <span className="text-sm font-bold text-primary">£{discountedFare}</span>
              <span className="text-xs text-muted-foreground">return fare</span>
            </div>
          )}
        </div>
      </div>

      {wantReturn && (
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <div>
            <label className="text-sm text-muted-foreground mb-1 block">Return Flight Number</label>
            <div className="relative">
              <Plane className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
              <Input
                placeholder="e.g. BA5678"
                value={returnFlightNumber}
                onChange={(e) => setReturnFlightNumber(e.target.value)}
                className="bg-secondary/50 pl-10"
              />
            </div>
          </div>
          <div>
            <label className="text-sm text-muted-foreground mb-1 block">
              Return Date <span className="text-destructive">*</span>
            </label>
            <div className="relative">
              <Calendar className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
              <Input
                type="date"
                value={returnDate}
                onChange={(e) => setReturnDate(e.target.value)}
                className="bg-secondary/50 pl-10"
              />
            </div>
          </div>
          <div>
            <label className="text-sm text-muted-foreground mb-1 block">
              Return Time <span className="text-destructive">*</span>
            </label>
            <div className="relative">
              <Clock className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
              <Input
                type="time"
                value={returnTime}
                onChange={(e) => setReturnTime(e.target.value)}
                className="bg-secondary/50 pl-10"
              />
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
