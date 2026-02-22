import { Plane, Calendar, Clock, Briefcase, MessageSquare, Minus, Plus } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";

interface FlightDetailsFormProps {
  flightNumber: string;
  setFlightNumber: (v: string) => void;
  travelDate: string;
  setTravelDate: (v: string) => void;
  travelTime: string;
  setTravelTime: (v: string) => void;
  suitcases: number;
  setSuitcases: (v: number) => void;
  handLuggage: number;
  setHandLuggage: (v: number) => void;
  specialInstructions: string;
  setSpecialInstructions: (v: string) => void;
}

function Counter({ label, value, onChange, icon: Icon }: { label: string; value: number; onChange: (v: number) => void; icon: React.ElementType }) {
  return (
    <div className="flex items-center justify-between bg-secondary/50 rounded-lg px-4 py-3">
      <div className="flex items-center gap-2">
        <Icon className="h-4 w-4 text-muted-foreground" />
        <span className="text-sm text-foreground">{label}</span>
      </div>
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={() => onChange(Math.max(0, value - 1))}
          className="h-7 w-7 rounded-full border border-border flex items-center justify-center text-muted-foreground hover:bg-muted transition-colors"
        >
          <Minus className="h-3 w-3" />
        </button>
        <span className="text-sm font-semibold text-foreground w-4 text-center">{value}</span>
        <button
          type="button"
          onClick={() => onChange(value + 1)}
          className="h-7 w-7 rounded-full border border-border flex items-center justify-center text-muted-foreground hover:bg-muted transition-colors"
        >
          <Plus className="h-3 w-3" />
        </button>
      </div>
    </div>
  );
}

export function FlightDetailsForm({
  flightNumber, setFlightNumber,
  travelDate, setTravelDate,
  travelTime, setTravelTime,
  suitcases, setSuitcases,
  handLuggage, setHandLuggage,
  specialInstructions, setSpecialInstructions,
}: FlightDetailsFormProps) {
  return (
    <section className="bg-card border border-border rounded-xl p-6 space-y-5">
      <h2 className="text-lg font-semibold text-foreground flex items-center gap-2">
        <Plane className="h-5 w-5 text-primary" />
        Flight & Travel Details
      </h2>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div>
          <label className="text-sm text-muted-foreground mb-1 block">Flight Number</label>
          <Input
            placeholder="e.g. BA1234"
            value={flightNumber}
            onChange={(e) => setFlightNumber(e.target.value)}
            className="bg-secondary/50"
          />
        </div>
        <div>
          <label className="text-sm text-muted-foreground mb-1 block">
            Travel Date <span className="text-destructive">*</span>
          </label>
          <div className="relative">
            <Calendar className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <Input
              type="date"
              value={travelDate}
              onChange={(e) => setTravelDate(e.target.value)}
              className="bg-secondary/50 pl-10"
            />
          </div>
        </div>
        <div>
          <label className="text-sm text-muted-foreground mb-1 block">
            Travel Time <span className="text-destructive">*</span>
          </label>
          <div className="relative">
            <Clock className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <Input
              type="time"
              value={travelTime}
              onChange={(e) => setTravelTime(e.target.value)}
              className="bg-secondary/50 pl-10"
            />
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <Counter label="Suitcases" value={suitcases} onChange={setSuitcases} icon={Briefcase} />
        <Counter label="Hand Luggage" value={handLuggage} onChange={setHandLuggage} icon={Briefcase} />
      </div>

      <div>
        <label className="text-sm text-muted-foreground mb-1 flex items-center gap-1">
          <MessageSquare className="h-3.5 w-3.5" />
          Special Instructions
        </label>
        <Textarea
          placeholder="e.g. Meet at arrivals, child seat needed, wheelchair access..."
          value={specialInstructions}
          onChange={(e) => setSpecialInstructions(e.target.value)}
          className="bg-secondary/50 min-h-[80px]"
        />
      </div>
    </section>
  );
}
