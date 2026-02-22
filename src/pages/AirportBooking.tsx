import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { supabase } from "@/integrations/supabase/client";
import { VehicleCard } from "@/components/airport-booking/VehicleCard";
import { FlightDetailsForm } from "@/components/airport-booking/FlightDetailsForm";
import { ReturnTripSection } from "@/components/airport-booking/ReturnTripSection";
import { BookingSummary } from "@/components/airport-booking/BookingSummary";
import { Loader2, CheckCircle2, AlertCircle } from "lucide-react";
import carzLogo from "@/assets/247carz-logo.png";
import { Button } from "@/components/ui/button";
import { useToast } from "@/hooks/use-toast";

interface BookingLink {
  id: string;
  token: string;
  caller_name: string | null;
  caller_phone: string | null;
  pickup: string | null;
  destination: string | null;
  passengers: number;
  fare_quotes: Record<string, { fare: string; eta_minutes?: number }>;
  return_discount_pct: number;
  status: string;
  company_id: string | null;
}

export interface VehicleOption {
  code: string;
  name: string;
  tier: "standard" | "executive";
  maxPassengers: number;
  maxSuitcases: number;
  maxHand: number;
  description: string;
  fare?: string;
}

const VEHICLE_OPTIONS: VehicleOption[] = [
  // Standard
  { code: "R4", name: "Standard Car", tier: "standard", maxPassengers: 3, maxSuitcases: 3, maxHand: 3, description: "3 passengers plus 3 standard size suitcases (23kg max). Dimensions: 90 x 75 x 43cm" },
  { code: "R6", name: "MPV", tier: "standard", maxPassengers: 4, maxSuitcases: 4, maxHand: 4, description: "4 passengers plus 4 standard size suitcases (23kg max). Dimensions: 90 x 75 x 43cm" },
  { code: "R7", name: "Large MPV", tier: "standard", maxPassengers: 5, maxSuitcases: 5, maxHand: 5, description: "5 passengers plus 5 standard size suitcases (23kg max). Dimensions: 90 x 75 x 43cm" },
  { code: "R8", name: "Standard Mini Bus", tier: "standard", maxPassengers: 16, maxSuitcases: 16, maxHand: 16, description: "Up to 16 passengers plus 16 standard size suitcases (23kg max). Dimensions: 90 x 75 x 43cm" },
  // Executive
  { code: "RE4", name: "Executive Saloon (E-Class)", tier: "executive", maxPassengers: 3, maxSuitcases: 3, maxHand: 3, description: "Mercedes E-Class. 3 passengers plus 3 standard size suitcases (23kg max)" },
  { code: "RE4S", name: "Executive Saloon (S-Class)", tier: "executive", maxPassengers: 3, maxSuitcases: 3, maxHand: 3, description: "Mercedes S-Class. 3 passengers plus 3 standard size suitcases (23kg max)" },
  { code: "RE7", name: "Executive MPV", tier: "executive", maxPassengers: 5, maxSuitcases: 5, maxHand: 5, description: "Mercedes V-Class or Vito. 5 passengers plus 5 standard size suitcases & 5 hand luggage" },
  { code: "RE8", name: "Executive Mini Bus", tier: "executive", maxPassengers: 16, maxSuitcases: 16, maxHand: 16, description: "Mercedes Sprinter minibus. 16 passengers plus 16 standard size suitcases & 16 hand luggage" },
];

const AirportBooking = () => {
  const { token } = useParams<{ token: string }>();
  const { toast } = useToast();
  const [loading, setLoading] = useState(true);
  const [booking, setBooking] = useState<BookingLink | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [selectedVehicle, setSelectedVehicle] = useState<string | null>(null);
  const [flightNumber, setFlightNumber] = useState("");
  const [travelDate, setTravelDate] = useState("");
  const [travelTime, setTravelTime] = useState("");
  const [suitcases, setSuitcases] = useState(0);
  const [handLuggage, setHandLuggage] = useState(0);
  const [specialInstructions, setSpecialInstructions] = useState("");

  // Return trip
  const [wantReturn, setWantReturn] = useState(false);
  const [returnDate, setReturnDate] = useState("");
  const [returnTime, setReturnTime] = useState("");
  const [returnFlightNumber, setReturnFlightNumber] = useState("");

  const [submitting, setSubmitting] = useState(false);
  const [submitted, setSubmitted] = useState(false);

  useEffect(() => {
    if (!token) return;
    const fetchBooking = async () => {
      const { data, error: err } = await supabase
        .from("airport_booking_links")
        .select("*")
        .eq("token", token)
        .single();

      if (err || !data) {
        setError("This booking link is invalid or has expired.");
        setLoading(false);
        return;
      }

      if (data.status !== "pending") {
        setError(data.status === "submitted" ? "This booking has already been submitted." : "This booking link has expired.");
        setLoading(false);
        return;
      }

      setBooking(data as unknown as BookingLink);
      setLoading(false);
    };
    fetchBooking();
  }, [token]);

  const handleSubmit = async () => {
    if (!selectedVehicle || !travelDate || !travelTime) {
      toast({ title: "Please fill required fields", description: "Vehicle type and travel date/time are required.", variant: "destructive" });
      return;
    }

    setSubmitting(true);
    const travelDatetime = new Date(`${travelDate}T${travelTime}`).toISOString();
    const returnDatetime = wantReturn && returnDate && returnTime ? new Date(`${returnDate}T${returnTime}`).toISOString() : null;

    const { error: updateErr } = await supabase
      .from("airport_booking_links")
      .update({
        vehicle_type: selectedVehicle,
        flight_number: flightNumber || null,
        travel_datetime: travelDatetime,
        luggage_suitcases: suitcases,
        luggage_hand: handLuggage,
        special_instructions: specialInstructions || null,
        return_trip: wantReturn,
        return_datetime: returnDatetime,
        return_flight_number: wantReturn ? returnFlightNumber || null : null,
        status: "submitted",
        submitted_at: new Date().toISOString(),
      })
      .eq("token", token)
      .eq("status", "pending");

    if (updateErr) {
      setSubmitting(false);
      toast({ title: "Error", description: "Failed to submit. Please try again.", variant: "destructive" });
      return;
    }

    // Dispatch to iCabbi
    try {
      const { data: dispatchResult, error: dispatchErr } = await supabase.functions.invoke(
        "airport-booking-dispatch",
        { body: { token } }
      );
      if (dispatchErr) {
        console.error("Dispatch error:", dispatchErr);
      } else {
        console.log("Dispatch result:", dispatchResult);
      }
    } catch (e) {
      console.error("Dispatch call failed:", e);
    }

    setSubmitting(false);
    setSubmitted(true);
  };

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background">
        <Loader2 className="h-8 w-8 animate-spin text-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background p-4">
        <div className="text-center max-w-md">
          <AlertCircle className="h-12 w-12 text-destructive mx-auto mb-4" />
          <h1 className="text-xl font-bold text-foreground mb-2">Booking Link Error</h1>
          <p className="text-muted-foreground">{error}</p>
        </div>
      </div>
    );
  }

  if (submitted) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background p-4">
        <div className="text-center max-w-md">
          <CheckCircle2 className="h-16 w-16 text-success mx-auto mb-4" />
          <h1 className="text-2xl font-bold text-foreground mb-2">Booking Submitted!</h1>
          <p className="text-muted-foreground mb-2">
            Your airport transfer has been submitted successfully. You will receive confirmation shortly.
          </p>
          {wantReturn && booking && (
            <p className="text-sm text-primary font-medium">
              🎉 {booking.return_discount_pct}% discount applied to your return trip!
            </p>
          )}
        </div>
      </div>
    );
  }

  if (!booking) return null;

  const vehiclesWithFares = VEHICLE_OPTIONS.map((v) => ({
    ...v,
    fare: booking.fare_quotes?.[v.code]?.fare,
  }));

  const standardVehicles = vehiclesWithFares.filter((v) => v.tier === "standard");
  const executiveVehicles = vehiclesWithFares.filter((v) => v.tier === "executive");
  const selectedVehicleData = vehiclesWithFares.find((v) => v.code === selectedVehicle);

  return (
    <div className="min-h-screen bg-background">
      {/* Header */}
      <header className="bg-card border-b border-border">
        <div className="max-w-4xl mx-auto px-4 py-6">
          <div className="flex items-center gap-3">
            <img src={carzLogo} alt="247 Carz" className="h-12 w-12 rounded-lg object-contain" />
            <div>
              <h1 className="font-display text-xl font-bold text-foreground">Airport Transfer Booking</h1>
              <p className="text-sm text-muted-foreground">Complete your booking details below</p>
            </div>
          </div>
        </div>
      </header>

      <main className="max-w-4xl mx-auto px-4 py-8 space-y-8">
        {/* Journey summary */}
        <BookingSummary booking={booking} />

        {/* Vehicle Selection - Standard */}
        <section>
          <h2 className="text-lg font-semibold text-foreground mb-1">Standard Vehicles</h2>
          <p className="text-sm text-muted-foreground mb-4">Select the vehicle that suits your group size and luggage</p>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
            {standardVehicles.map((vehicle) => (
              <VehicleCard
                key={vehicle.code}
                vehicle={vehicle}
                selected={selectedVehicle === vehicle.code}
                onSelect={() => setSelectedVehicle(vehicle.code)}
              />
            ))}
          </div>
        </section>

        {/* Vehicle Selection - Executive */}
        <section>
          <h2 className="text-lg font-semibold text-foreground mb-1">Executive Vehicles</h2>
          <p className="text-sm text-muted-foreground mb-4">Premium Mercedes fleet for a luxury experience</p>
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
            {executiveVehicles.map((vehicle) => (
              <VehicleCard
                key={vehicle.code}
                vehicle={vehicle}
                selected={selectedVehicle === vehicle.code}
                onSelect={() => setSelectedVehicle(vehicle.code)}
              />
            ))}
          </div>
        </section>

        {/* Flight Details */}
        <FlightDetailsForm
          flightNumber={flightNumber}
          setFlightNumber={setFlightNumber}
          travelDate={travelDate}
          setTravelDate={setTravelDate}
          travelTime={travelTime}
          setTravelTime={setTravelTime}
          suitcases={suitcases}
          setSuitcases={setSuitcases}
          handLuggage={handLuggage}
          setHandLuggage={setHandLuggage}
          specialInstructions={specialInstructions}
          setSpecialInstructions={setSpecialInstructions}
        />

        {/* Return Trip */}
        <ReturnTripSection
          wantReturn={wantReturn}
          setWantReturn={setWantReturn}
          returnDate={returnDate}
          setReturnDate={setReturnDate}
          returnTime={returnTime}
          setReturnTime={setReturnTime}
          returnFlightNumber={returnFlightNumber}
          setReturnFlightNumber={setReturnFlightNumber}
          discountPct={booking.return_discount_pct}
          selectedVehicleFare={selectedVehicleData?.fare}
        />

        {/* Submit */}
        <div className="flex justify-center pt-4 pb-12">
          <Button
            onClick={handleSubmit}
            disabled={submitting || !selectedVehicle || !travelDate || !travelTime}
            className="bg-gradient-gold hover:opacity-90 text-primary-foreground px-12 py-6 text-lg font-semibold rounded-xl"
          >
            {submitting ? (
              <>
                <Loader2 className="mr-2 h-5 w-5 animate-spin" />
                Submitting...
              </>
            ) : (
              "Confirm Booking"
            )}
          </Button>
        </div>
      </main>
    </div>
  );
};

export default AirportBooking;
