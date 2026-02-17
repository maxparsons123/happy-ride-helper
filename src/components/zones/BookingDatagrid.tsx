import { useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Phone, MapPin, Users, DollarSign, Clock, Trash2, Radio } from 'lucide-react';
import type { MqttBooking } from '@/hooks/use-mqtt-dispatch';

interface BookingDatagridProps {
  bookings: MqttBooking[];
  connectionStatus: string;
  onSelectBooking?: (booking: MqttBooking) => void;
  selectedBookingId?: string;
  onClearCompleted?: () => void;
}

const statusColors: Record<string, string> = {
  pending: 'bg-yellow-500/20 text-yellow-700 border-yellow-500/30',
  allocated: 'bg-blue-500/20 text-blue-700 border-blue-500/30',
  completed: 'bg-green-500/20 text-green-700 border-green-500/30',
  cancelled: 'bg-red-500/20 text-red-700 border-red-500/30',
};

function timeAgo(ts: number) {
  const sec = Math.floor((Date.now() - ts) / 1000);
  if (sec < 60) return `${sec}s ago`;
  if (sec < 3600) return `${Math.floor(sec / 60)}m ago`;
  return `${Math.floor(sec / 3600)}h ago`;
}

export function BookingDatagrid({
  bookings,
  connectionStatus,
  onSelectBooking,
  selectedBookingId,
  onClearCompleted,
}: BookingDatagridProps) {
  const mqttDot = connectionStatus === 'connected'
    ? 'bg-green-500 shadow-[0_0_6px_rgba(34,197,94,0.6)]'
    : connectionStatus === 'connecting'
    ? 'bg-yellow-500 animate-pulse'
    : 'bg-red-500';

  return (
    <div className="flex flex-col h-full bg-background border-t">
      {/* Header */}
      <div className="px-3 py-2 border-b flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Radio className="w-4 h-4 text-primary" />
          <span className="font-semibold text-sm">Live Bookings</span>
          <span className={`w-2 h-2 rounded-full ${mqttDot}`} />
          <span className="text-xs text-muted-foreground">
            {connectionStatus === 'connected' ? 'MQTT' : connectionStatus}
          </span>
          {bookings.length > 0 && (
            <Badge variant="secondary" className="text-[10px] h-5 px-1.5">
              {bookings.length}
            </Badge>
          )}
        </div>
        {onClearCompleted && bookings.some(b => b.status === 'completed' || b.status === 'cancelled') && (
          <Button variant="ghost" size="sm" className="h-6 text-xs" onClick={onClearCompleted}>
            <Trash2 className="w-3 h-3 mr-1" /> Clear
          </Button>
        )}
      </div>

      {/* Grid */}
      <ScrollArea className="flex-1">
        {bookings.length === 0 && (
          <div className="p-4 text-center text-xs text-muted-foreground">
            {connectionStatus === 'connected'
              ? 'Waiting for bookings via MQTT...'
              : 'Connecting to MQTT broker...'}
          </div>
        )}
        {bookings.map(booking => (
          <div
            key={booking.id}
            className={`px-3 py-2 border-b cursor-pointer hover:bg-accent/50 transition-colors text-xs ${
              selectedBookingId === booking.id ? 'bg-accent' : ''
            }`}
            onClick={() => onSelectBooking?.(booking)}
          >
            <div className="flex items-center justify-between mb-1">
              <div className="flex items-center gap-1.5">
                <span className="font-mono font-bold text-[11px]">{booking.jobId.slice(-6).toUpperCase()}</span>
                <Badge className={`text-[9px] h-4 px-1 border ${statusColors[booking.status] || ''}`}>
                  {booking.status}
                </Badge>
              </div>
              <span className="text-muted-foreground text-[10px]">
                <Clock className="w-3 h-3 inline mr-0.5" />
                {timeAgo(booking.receivedAt)}
              </span>
            </div>
            <div className="grid grid-cols-2 gap-x-2 gap-y-0.5">
              <div className="flex items-center gap-1 truncate">
                <MapPin className="w-3 h-3 text-green-600 flex-shrink-0" />
                <span className="truncate">{booking.pickup}</span>
              </div>
              <div className="flex items-center gap-1 truncate">
                <MapPin className="w-3 h-3 text-red-500 flex-shrink-0" />
                <span className="truncate">{booking.dropoff}</span>
              </div>
              <div className="flex items-center gap-1">
                <Users className="w-3 h-3 text-muted-foreground" />
                <span>{booking.passengers}</span>
              </div>
              <div className="flex items-center gap-1">
                <DollarSign className="w-3 h-3 text-muted-foreground" />
                <span>£{booking.fare}</span>
              </div>
              <div className="flex items-center gap-1 truncate">
                <Phone className="w-3 h-3 text-muted-foreground flex-shrink-0" />
                <span className="truncate">{booking.customerName}</span>
              </div>
              {booking.notes && booking.notes !== 'None' && (
                <div className="text-muted-foreground truncate col-span-2">
                  📝 {booking.notes}
                </div>
              )}
            </div>
          </div>
        ))}
      </ScrollArea>
    </div>
  );
}
