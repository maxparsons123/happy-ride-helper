import { useEffect, useRef, useCallback, useState } from 'react';
import mqtt from 'mqtt';
import { supabase } from '@/integrations/supabase/client';

const BROKER_URL = 'wss://broker.hivemq.com:8884/mqtt';

export interface DriverBid {
  driverId: string;
  jobId: string;
  lat: number;
  lng: number;
  timestamp: number;
  pickupAddress?: string;
  dropoff?: string;
  fare?: string;
}

export interface MqttBooking {
  id: string;
  jobId: string;
  pickup: string;
  dropoff: string;
  pickupLat: number;
  pickupLng: number;
  dropoffLat: number;
  dropoffLng: number;
  passengers: string;
  fare: string;
  customerName: string;
  customerPhone: string;
  notes: string;
  receivedAt: number;
  status: 'pending' | 'allocated' | 'completed' | 'cancelled';
}

export interface OnlineDriver {
  id: string;
  name: string;
  status: string;
  vehicle: string;
  registration: string;
  lat: number;
  lng: number;
  lastSeen: number;
}

export function useMqttDispatch() {
  // Persist a driver bid to the bookings table bids JSONB array
  const persistBidToDb = useCallback(async (bid: DriverBid) => {
    try {
      // Fetch current bids array
      const { data: booking } = await supabase
        .from('bookings')
        .select('bids')
        .or(`call_id.eq.${bid.jobId},id.eq.${bid.jobId}`)
        .maybeSingle();

      const currentBids = (booking?.bids as any[]) || [];
      // Avoid duplicates
      if (currentBids.some((b: any) => b.driverId === bid.driverId)) return;

      const newBid = {
        driverId: bid.driverId,
        lat: bid.lat,
        lng: bid.lng,
        pickupAddress: bid.pickupAddress,
        dropoff: bid.dropoff,
        fare: bid.fare,
        bidTime: new Date().toISOString(),
      };

      await supabase
        .from('bookings')
        .update({ bids: [...currentBids, newBid] })
        .or(`call_id.eq.${bid.jobId},id.eq.${bid.jobId}`);
    } catch (e) {
      console.error('Failed to persist bid to DB:', e);
    }
  }, []);

  const clientRef = useRef<mqtt.MqttClient | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<'connecting' | 'connected' | 'offline' | 'error'>('connecting');
  const [bookings, setBookings] = useState<MqttBooking[]>([]);
  const [onlineDrivers, setOnlineDrivers] = useState<OnlineDriver[]>([]);
  const [incomingBids, setIncomingBids] = useState<DriverBid[]>([]);
  const webrtcHandlerRef = useRef<((topic: string, data: any) => boolean) | null>(null);
  useEffect(() => {
    const clientId = `dispatch_${Math.random().toString(36).substr(2, 8)}`;
    const client = mqtt.connect(BROKER_URL, {
      clientId,
      reconnectPeriod: 2000,
      connectTimeout: 10000,
      keepalive: 60,
      clean: true,
      protocolVersion: 4,
    });
    clientRef.current = client;

    client.on('connect', () => {
      setConnectionStatus('connected');
      client.subscribe('taxi/bookings', { qos: 1 });
      client.subscribe('drivers/+/status');
      client.subscribe('drivers/+/location');
      client.subscribe('radio/channel');
      client.subscribe('dispatch/bids/incoming');
      client.subscribe('jobs/+/bids');
      // WebRTC radio signaling
      client.subscribe('radio/webrtc/signal/DISPATCH');
      client.subscribe('radio/webrtc/presence');
    });

    client.on('message', (topic: string, message: Buffer) => {
      try {
        const data = JSON.parse(message.toString());

        // Route WebRTC signaling/presence to radio hook
        if (topic.startsWith('radio/webrtc/')) {
          webrtcHandlerRef.current?.(topic, data);
          return;
        }
        // Driver status/location updates
        if (topic.startsWith('drivers/') && (topic.endsWith('/status') || topic.endsWith('/location'))) {
          const driverId = topic.split('/')[1];
          if (driverId === 'DISPATCH') return;
          setOnlineDrivers(prev => {
            const idx = prev.findIndex(d => d.id === driverId);
            const driver: OnlineDriver = {
              id: driverId,
              name: data.name || driverId,
              status: data.status || 'available',
              vehicle: data.vehicle || '',
              registration: data.registration || '',
              lat: data.lat || 0,
              lng: data.lng || 0,
              lastSeen: Date.now(),
            };
            if (idx >= 0) {
              const updated = [...prev];
              updated[idx] = driver;
              return updated;
            }
            return [...prev, driver];
          });
          return;
        }

        // Driver bids
        if (topic === 'dispatch/bids/incoming' || topic.match(/^jobs\/.*\/bids$/)) {
          if (data.driverId && data.jobId) {
            const bid: DriverBid = {
              driverId: data.driverId,
              jobId: data.jobId,
              lat: data.lat || 0,
              lng: data.lng || 0,
              timestamp: data.timestamp || Date.now(),
              pickupAddress: data.pickupAddress,
              dropoff: data.dropoff,
              fare: data.fare,
            };
            setIncomingBids(prev => {
              const exists = prev.find(b => b.driverId === bid.driverId && b.jobId === bid.jobId);
              if (exists) return prev;
              // Persist bid to bookings table
              persistBidToDb(bid);
              return [bid, ...prev].slice(0, 100);
            });
          }
          return;
        }

        // Bookings
        if (topic === 'taxi/bookings') {
          const booking: MqttBooking = {
            id: data.jobId || data.job || `mqtt_${Date.now()}`,
            jobId: data.jobId || data.job || '',
            pickup: data.pickup || 'Unknown',
            dropoff: data.dropoff || 'Unknown',
            pickupLat: parseFloat(data.pickupLat) || 0,
            pickupLng: parseFloat(data.pickupLng) || 0,
            dropoffLat: parseFloat(data.dropoffLat) || parseFloat(data.dropoffLng) || 0,
            dropoffLng: parseFloat(data.dropoffLng) || 0,
            passengers: data.passengers || '1',
            fare: data.fare || data.estimatedFare || data.estimatedPrice || data.price || data.amount || data.cost || '0.00',
            customerName: data.customerName || 'Customer',
            customerPhone: data.customerPhone || '',
            notes: data.notes || '',
            receivedAt: Date.now(),
            status: 'pending',
          };

          setBookings(prev => {
            const exists = prev.findIndex(b => b.jobId === booking.jobId);
            if (exists >= 0) {
              const updated = [...prev];
              updated[exists] = { ...updated[exists], ...booking, status: updated[exists].status };
              return updated;
            }
            return [booking, ...prev];
          });
        }
      } catch (e) {
        console.error('MQTT dispatch parse error:', e);
      }
    });

    client.on('error', () => setConnectionStatus('error'));
    client.on('reconnect', () => setConnectionStatus('connecting'));
    client.on('offline', () => setConnectionStatus('offline'));

    // Prune stale drivers every 30s
    const pruneInterval = setInterval(() => {
      const cutoff = Date.now() - 60000;
      setOnlineDrivers(prev => prev.filter(d => d.lastSeen > cutoff));
    }, 30000);

    return () => {
      clearInterval(pruneInterval);
      client.end(true);
    };
  }, []);

  const updateBookingStatus = useCallback((jobId: string, status: MqttBooking['status']) => {
    setBookings(prev => prev.map(b => b.jobId === jobId ? { ...b, status } : b));
  }, []);

  const clearCompleted = useCallback(() => {
    setBookings(prev => prev.filter(b => b.status === 'pending' || b.status === 'allocated'));
  }, []);

  const publish = useCallback((topic: string, payload: any) => {
    if (clientRef.current?.connected) {
      clientRef.current.publish(topic, JSON.stringify(payload));
    }
  }, []);

  const setWebRtcHandler = useCallback((handler: (topic: string, data: any) => boolean) => {
    webrtcHandlerRef.current = handler;
  }, []);

  return { connectionStatus, bookings, updateBookingStatus, clearCompleted, publish, onlineDrivers, incomingBids, setWebRtcHandler };
}
