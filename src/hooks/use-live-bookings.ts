import { useEffect, useState } from 'react';
import { supabase } from '@/integrations/supabase/client';

export interface LiveBookingMarker {
  id: string;
  pickup: string | null;
  destination: string | null;
  passengers: number;
  caller_phone: string | null;
  caller_name: string | null;
  status: string;
  lat: number;
  lng: number;
  dest_lat?: number;
  dest_lng?: number;
  created_at: string;
}

export function useLiveBookings() {
  const [bookings, setBookings] = useState<LiveBookingMarker[]>([]);

  useEffect(() => {
    // Load active live_calls with GPS
    const loadActive = async () => {
      const { data } = await supabase
        .from('live_calls')
        .select('id, call_id, pickup, destination, passengers, caller_phone, caller_name, status, gps_lat, gps_lon, started_at')
        .eq('status', 'active')
        .not('gps_lat', 'is', null);

      if (data) {
        setBookings(data.filter(d => d.gps_lat && d.gps_lon).map(d => ({
          id: d.id,
          pickup: d.pickup,
          destination: d.destination,
          passengers: d.passengers || 1,
          caller_phone: d.caller_phone,
          caller_name: d.caller_name,
          status: d.status,
          lat: d.gps_lat!,
          lng: d.gps_lon!,
          created_at: d.started_at,
        })));
      }
    };

    loadActive();

    // Subscribe to live_calls changes
    const channel = supabase
      .channel('zone-map-live-calls')
      .on('postgres_changes', {
        event: '*',
        schema: 'public',
        table: 'live_calls',
      }, (payload) => {
        const row = payload.new as any;
        if (!row) return;

        if (payload.eventType === 'DELETE' || row.status === 'ended') {
          setBookings(prev => prev.filter(b => b.id !== (payload.old as any)?.id && b.id !== row?.id));
          return;
        }

        if (!row.gps_lat || !row.gps_lon) return;

        const marker: LiveBookingMarker = {
          id: row.id,
          pickup: row.pickup,
          destination: row.destination,
          passengers: row.passengers || 1,
          caller_phone: row.caller_phone,
          caller_name: row.caller_name,
          status: row.status,
          lat: row.gps_lat,
          lng: row.gps_lon,
          created_at: row.started_at,
        };

        setBookings(prev => {
          const exists = prev.findIndex(b => b.id === marker.id);
          if (exists >= 0) {
            const updated = [...prev];
            updated[exists] = marker;
            return updated;
          }
          return [...prev, marker];
        });
      })
      .subscribe();

    return () => { supabase.removeChannel(channel); };
  }, []);

  return bookings;
}
