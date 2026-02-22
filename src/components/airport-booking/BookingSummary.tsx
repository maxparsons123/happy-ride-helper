import { MapPin, Users, Phone, User } from "lucide-react";

interface BookingSummaryProps {
  booking: {
    caller_name: string | null;
    caller_phone: string | null;
    pickup: string | null;
    destination: string | null;
    passengers: number;
  };
}

export function BookingSummary({ booking }: BookingSummaryProps) {
  return (
    <section className="bg-card border border-border rounded-xl p-6">
      <h2 className="text-lg font-semibold text-foreground mb-4">Your Journey</h2>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 text-sm">
        {booking.caller_name && (
          <div className="flex items-center gap-2">
            <User className="h-4 w-4 text-primary flex-shrink-0" />
            <span className="text-muted-foreground">Name:</span>
            <span className="text-foreground font-medium">{booking.caller_name}</span>
          </div>
        )}
        {booking.caller_phone && (
          <div className="flex items-center gap-2">
            <Phone className="h-4 w-4 text-primary flex-shrink-0" />
            <span className="text-muted-foreground">Phone:</span>
            <span className="text-foreground font-medium">{booking.caller_phone}</span>
          </div>
        )}
        {booking.pickup && (
          <div className="flex items-center gap-2">
            <MapPin className="h-4 w-4 text-success flex-shrink-0" />
            <span className="text-muted-foreground">Pickup:</span>
            <span className="text-foreground font-medium">{booking.pickup}</span>
          </div>
        )}
        {booking.destination && (
          <div className="flex items-center gap-2">
            <MapPin className="h-4 w-4 text-destructive flex-shrink-0" />
            <span className="text-muted-foreground">Destination:</span>
            <span className="text-foreground font-medium">{booking.destination}</span>
          </div>
        )}
        <div className="flex items-center gap-2">
          <Users className="h-4 w-4 text-primary flex-shrink-0" />
          <span className="text-muted-foreground">Passengers:</span>
          <span className="text-foreground font-medium">{booking.passengers}</span>
        </div>
      </div>
    </section>
  );
}
