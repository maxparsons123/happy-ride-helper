import { useEffect, useRef, useCallback, useState } from 'react';
import mqtt from 'mqtt';

const BROKER_URL = 'wss://broker.hivemq.com:8884/mqtt';

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

export function useMqttDispatch() {
  const clientRef = useRef<mqtt.MqttClient | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<'connecting' | 'connected' | 'offline' | 'error'>('connecting');
  const [bookings, setBookings] = useState<MqttBooking[]>([]);

  useEffect(() => {
    const clientId = `dispatch_${Math.random().toString(36).substr(2, 8)}`;
    const client = mqtt.connect(BROKER_URL, {
      clientId,
      reconnectPeriod: 2000,
      connectTimeout: 10000,
      keepalive: 60,
      clean: true,
    });
    clientRef.current = client;

    client.on('connect', () => {
      setConnectionStatus('connected');
      // Subscribe to the same topic the C# bridge publishes to
      client.subscribe('taxi/bookings', { qos: 1 });
    });

    client.on('message', (_topic: string, message: Buffer) => {
      try {
        const data = JSON.parse(message.toString());
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
          fare: data.fare || '0.00',
          customerName: data.customerName || 'Customer',
          customerPhone: data.customerPhone || '',
          notes: data.notes || '',
          receivedAt: Date.now(),
          status: 'pending',
        };

        setBookings(prev => {
          // Deduplicate by jobId
          const exists = prev.findIndex(b => b.jobId === booking.jobId);
          if (exists >= 0) {
            const updated = [...prev];
            updated[exists] = { ...updated[exists], ...booking, status: updated[exists].status };
            return updated;
          }
          return [booking, ...prev];
        });
      } catch (e) {
        console.error('MQTT dispatch parse error:', e);
      }
    });

    client.on('error', () => setConnectionStatus('error'));
    client.on('reconnect', () => setConnectionStatus('connecting'));
    client.on('offline', () => setConnectionStatus('offline'));

    return () => { client.end(true); };
  }, []);

  const updateBookingStatus = useCallback((jobId: string, status: MqttBooking['status']) => {
    setBookings(prev => prev.map(b => b.jobId === jobId ? { ...b, status } : b));
  }, []);

  const clearCompleted = useCallback(() => {
    setBookings(prev => prev.filter(b => b.status === 'pending' || b.status === 'allocated'));
  }, []);

  return { connectionStatus, bookings, updateBookingStatus, clearCompleted };
}
