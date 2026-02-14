import { useEffect, useRef, useCallback, useState } from 'react';
import mqtt from 'mqtt';

const BROKER_URL = 'wss://broker.hivemq.com:8884/mqtt';

interface UseMqttDriverOptions {
  driverId: string;
  onJobRequest: (topic: string, data: any) => void;
  onJobResult: (topic: string, data: any) => void;
}

export function useMqttDriver({ driverId, onJobRequest, onJobResult }: UseMqttDriverOptions) {
  const clientRef = useRef<mqtt.MqttClient | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<'connecting' | 'connected' | 'offline' | 'error'>('connecting');

  const onJobRequestRef = useRef(onJobRequest);
  const onJobResultRef = useRef(onJobResult);
  onJobRequestRef.current = onJobRequest;
  onJobResultRef.current = onJobResult;

  useEffect(() => {
    const clientId = `driver_${driverId}_${Math.random().toString(36).substr(2, 8)}`;
    const client = mqtt.connect(BROKER_URL, {
      clientId, reconnectPeriod: 2000, connectTimeout: 10000, keepalive: 60, clean: true,
    });
    clientRef.current = client;

    client.on('connect', () => {
      setConnectionStatus('connected');
      client.subscribe('pubs/requests/+');
      client.subscribe(`jobs/+/result/${driverId}`);
      client.subscribe(`drivers/${driverId}/bid-request`);
      client.subscribe(`drivers/${driverId}/jobs`);
    });

    client.on('message', (topic: string, message: Buffer) => {
      try {
        const data = JSON.parse(message.toString());
        if (topic.startsWith('pubs/requests/') || topic.includes('/bid-request') || topic.includes('/jobs')) {
          onJobRequestRef.current(topic, data);
        }
        if (topic.includes(`/result/${driverId}`)) {
          onJobResultRef.current(topic, data);
        }
      } catch (e) {
        console.error('MQTT parse error:', e);
      }
    });

    client.on('error', () => setConnectionStatus('error'));
    client.on('reconnect', () => setConnectionStatus('connecting'));
    client.on('offline', () => setConnectionStatus('offline'));

    return () => { client.end(true); };
  }, [driverId]);

  const publish = useCallback((topic: string, payload: any) => {
    clientRef.current?.publish(topic, JSON.stringify(payload));
  }, []);

  return { connectionStatus, publish };
}
