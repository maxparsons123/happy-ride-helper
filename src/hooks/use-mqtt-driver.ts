import { useEffect, useRef, useCallback, useState } from 'react';
import mqtt from 'mqtt';

const BROKER_URL = 'wss://broker.hivemq.com:8884/mqtt';

interface UseMqttDriverOptions {
  driverId: string;
  onJobRequest: (topic: string, data: any) => void;
  onJobResult: (topic: string, data: any) => void;
}

export interface RadioMessage {
  driver: string;
  name: string;
  audio: string;
  mime: string;
  ts: number;
  targets?: string[];
}

export function useMqttDriver({ driverId, onJobRequest, onJobResult }: UseMqttDriverOptions) {
  const clientRef = useRef<mqtt.MqttClient | null>(null);
  const [connectionStatus, setConnectionStatus] = useState<'connecting' | 'connected' | 'offline' | 'error'>('connecting');
  const [lastRadioMessage, setLastRadioMessage] = useState<RadioMessage | null>(null);
  const [remotePttState, setRemotePttState] = useState<{ from: string; name: string; active: boolean } | null>(null);
  const webrtcHandlerRef = useRef<((topic: string, data: any) => boolean) | null>(null);

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
      // Radio topics (legacy + WebRTC signaling)
      client.subscribe('radio/broadcast');
      client.subscribe('radio/channel');
      client.subscribe(`radio/driver/${driverId}`);
      client.subscribe(`radio/webrtc/signal/${driverId}`);
      client.subscribe('radio/webrtc/presence');
      client.subscribe('radio/ptt-state');
    });

    client.on('message', (topic: string, message: Buffer) => {
      try {
        const data = JSON.parse(message.toString());

        // Route WebRTC signaling/presence to radio hook
        if (topic.startsWith('radio/webrtc/')) {
          webrtcHandlerRef.current?.(topic, data);
          return;
        }

        // PTT state messages
        if (topic === 'radio/ptt-state') {
          if (data.from !== driverId) {
            setRemotePttState({ from: data.from, name: data.name || data.from, active: data.active });
            if (!data.active) {
              // Clear after a short delay so UI flashes
              setTimeout(() => setRemotePttState(null), 500);
            }
          }
          return;
        }

        // Radio messages
        if (topic === 'radio/broadcast' || topic === 'radio/channel' || topic === `radio/driver/${driverId}`) {
          if (data.driver !== driverId && data.audio) {
            if (topic === 'radio/broadcast' && data.targets && Array.isArray(data.targets)) {
              if (!data.targets.includes(driverId)) return;
            }
            setLastRadioMessage(data as RadioMessage);
          }
          return;
        }

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

  const setWebRtcHandler = useCallback((handler: (topic: string, data: any) => boolean) => {
    webrtcHandlerRef.current = handler;
  }, []);

  return { connectionStatus, publish, lastRadioMessage, remotePttState, setWebRtcHandler };
}
