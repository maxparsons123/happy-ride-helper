import { useState, useRef, useCallback, useEffect } from 'react';

const ICE_SERVERS = [
  { urls: 'stun:stun.l.google.com:19302' },
  { urls: 'stun:stun1.l.google.com:19302' },
];

interface PeerData {
  pc: RTCPeerConnection;
  pendingCandidates: RTCIceCandidateInit[];
  audioEl: HTMLAudioElement | null;
}

interface WebRTCSignal {
  type: 'offer' | 'answer' | 'ice-candidate';
  payload: RTCSessionDescriptionInit | RTCIceCandidateInit;
  from: string;
  to: string;
}

interface PresenceMessage {
  type: 'join' | 'here' | 'leave';
  peerId: string;
  name?: string;
  ts: number;
}

export interface UseWebRTCRadioOptions {
  peerId: string;
  peerName: string;
  publish: (topic: string, payload: any) => void;
  mqttConnected: boolean;
}

export function useWebRTCRadio({ peerId, peerName, publish, mqttConnected }: UseWebRTCRadioOptions) {
  const [connectedPeers, setConnectedPeers] = useState<string[]>([]);
  const [isTransmitting, setIsTransmitting] = useState(false);
  const [localStream, setLocalStream] = useState<MediaStream | null>(null);

  const peersRef = useRef<Map<string, PeerData>>(new Map());
  const localStreamRef = useRef<MediaStream | null>(null);
  const volumeRef = useRef(0.8);
  const peerIdRef = useRef(peerId);
  peerIdRef.current = peerId;

  // Keep-alive oscillator to prevent tab throttling
  const bgCtxRef = useRef<AudioContext | null>(null);

  const startBackgroundKeepAlive = useCallback(() => {
    if (bgCtxRef.current) return;
    try {
      const ctx = new AudioContext();
      const gain = ctx.createGain();
      gain.gain.value = 0.001;
      const osc = ctx.createOscillator();
      osc.frequency.value = 1;
      osc.connect(gain);
      gain.connect(ctx.destination);
      osc.start();
      bgCtxRef.current = ctx;
    } catch { }
  }, []);

  const updateConnectedPeers = useCallback(() => {
    const connected: string[] = [];
    peersRef.current.forEach((data, remotePeerId) => {
      if (data.pc.connectionState === 'connected') {
        connected.push(remotePeerId);
      }
    });
    setConnectedPeers(connected);
  }, []);

  const cleanupPeer = useCallback((remotePeerId: string) => {
    const data = peersRef.current.get(remotePeerId);
    if (data) {
      if (data.audioEl) {
        data.audioEl.pause();
        data.audioEl.remove();
      }
      try { data.pc.close(); } catch { }
      peersRef.current.delete(remotePeerId);
    }
    updateConnectedPeers();
  }, [updateConnectedPeers]);

  // Start local media
  const startMedia = useCallback(async () => {
    if (localStreamRef.current) return localStreamRef.current;
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
      video: false,
    });
    // Mute by default — PTT enables
    stream.getAudioTracks().forEach(t => { t.enabled = false; });
    localStreamRef.current = stream;
    setLocalStream(stream);
    return stream;
  }, []);

  // Create peer connection
  const createPeerConnection = useCallback((remotePeerId: string): RTCPeerConnection => {
    const existing = peersRef.current.get(remotePeerId);
    if (existing && existing.pc.connectionState !== 'closed' && existing.pc.connectionState !== 'failed') {
      return existing.pc;
    }

    const pc = new RTCPeerConnection({ iceServers: ICE_SERVERS });
    const peerData: PeerData = { pc, pendingCandidates: [], audioEl: null };

    pc.onicecandidate = (event) => {
      if (event.candidate) {
        publish(`radio/webrtc/signal/${remotePeerId}`, {
          type: 'ice-candidate',
          payload: event.candidate.toJSON(),
          from: peerIdRef.current,
          to: remotePeerId,
        });
      }
    };

    pc.ontrack = (event) => {
      const stream = event.streams?.[0] ?? new MediaStream([event.track]);
      if (!peerData.audioEl) {
        const audio = document.createElement('audio');
        audio.autoplay = true;
        (audio as any).playsInline = true;
        audio.volume = volumeRef.current;
        document.body.appendChild(audio);
        peerData.audioEl = audio;
      }
      peerData.audioEl.srcObject = stream;
      peerData.audioEl.play().catch(() => { });
    };

    pc.onconnectionstatechange = () => {
      updateConnectedPeers();
      if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
        cleanupPeer(remotePeerId);
      }
    };

    // Add local tracks
    if (localStreamRef.current) {
      localStreamRef.current.getTracks().forEach(track => {
        pc.addTrack(track, localStreamRef.current!);
      });
    }

    peersRef.current.set(remotePeerId, peerData);
    return pc;
  }, [publish, updateConnectedPeers, cleanupPeer]);

  // Initiate connection
  const connectToPeer = useCallback(async (remotePeerId: string) => {
    if (!localStreamRef.current) await startMedia();
    if (!localStreamRef.current) return;

    const pc = createPeerConnection(remotePeerId);
    const senders = pc.getSenders();
    localStreamRef.current.getTracks().forEach(track => {
      if (!senders.find(s => s.track === track)) {
        pc.addTrack(track, localStreamRef.current!);
      }
    });

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);

    publish(`radio/webrtc/signal/${remotePeerId}`, {
      type: 'offer',
      payload: offer,
      from: peerIdRef.current,
      to: remotePeerId,
    });
  }, [startMedia, createPeerConnection, publish]);

  // Handle incoming signal
  const handleSignal = useCallback(async (signal: WebRTCSignal) => {
    const remotePeerId = signal.from;
    if (remotePeerId === peerIdRef.current) return;
    if (signal.to && signal.to !== peerIdRef.current) return;

    const pc = createPeerConnection(remotePeerId);
    const peerData = peersRef.current.get(remotePeerId);

    try {
      if (signal.type === 'offer') {
        const isPolite = peerIdRef.current < remotePeerId;
        if (pc.signalingState !== 'stable') {
          if (!isPolite) return;
          await pc.setLocalDescription({ type: 'rollback' });
        }

        if (!localStreamRef.current) await startMedia();
        if (localStreamRef.current) {
          const senders = pc.getSenders();
          localStreamRef.current.getTracks().forEach(track => {
            if (!senders.find(s => s.track === track)) {
              pc.addTrack(track, localStreamRef.current!);
            }
          });
        }

        await pc.setRemoteDescription(new RTCSessionDescription(signal.payload as RTCSessionDescriptionInit));

        if (peerData) {
          for (const c of peerData.pendingCandidates) {
            await pc.addIceCandidate(new RTCIceCandidate(c));
          }
          peerData.pendingCandidates = [];
        }

        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);

        publish(`radio/webrtc/signal/${remotePeerId}`, {
          type: 'answer',
          payload: answer,
          from: peerIdRef.current,
          to: remotePeerId,
        });
      } else if (signal.type === 'answer') {
        if (pc.signalingState !== 'have-local-offer') return;
        await pc.setRemoteDescription(new RTCSessionDescription(signal.payload as RTCSessionDescriptionInit));

        if (peerData) {
          for (const c of peerData.pendingCandidates) {
            await pc.addIceCandidate(new RTCIceCandidate(c));
          }
          peerData.pendingCandidates = [];
        }
      } else if (signal.type === 'ice-candidate') {
        if (!pc.remoteDescription) {
          peerData?.pendingCandidates.push(signal.payload as RTCIceCandidateInit);
          return;
        }
        await pc.addIceCandidate(new RTCIceCandidate(signal.payload as RTCIceCandidateInit));
      }
    } catch (e) {
      console.error('[WebRTC Radio] Signal error:', e);
    }
  }, [createPeerConnection, startMedia, publish]);

  // Handle presence
  const handlePresence = useCallback((data: PresenceMessage) => {
    if (data.peerId === peerIdRef.current) return;

    if (data.type === 'join' || data.type === 'here') {
      const shouldInitiate = peerIdRef.current < data.peerId;
      const existing = peersRef.current.get(data.peerId);
      const alreadyConnected = existing && existing.pc.connectionState !== 'closed' && existing.pc.connectionState !== 'failed';

      if (shouldInitiate && !alreadyConnected) {
        connectToPeer(data.peerId).catch(console.error);
      }

      if (data.type === 'join') {
        // Reply so the new peer knows about us
        publish('radio/webrtc/presence', {
          type: 'here' as const,
          peerId: peerIdRef.current,
          name: peerName,
          ts: Date.now(),
        });
      }
    } else if (data.type === 'leave') {
      cleanupPeer(data.peerId);
    }
  }, [connectToPeer, cleanupPeer, publish, peerName]);

  // Process incoming MQTT message — call from parent's MQTT handler
  const handleMqttMessage = useCallback((topic: string, data: any): boolean => {
    if (topic === `radio/webrtc/signal/${peerIdRef.current}`) {
      handleSignal(data as WebRTCSignal);
      return true;
    }
    if (topic === 'radio/webrtc/presence') {
      handlePresence(data as PresenceMessage);
      return true;
    }
    return false;
  }, [handleSignal, handlePresence]);

  // Join the radio channel
  const joinChannel = useCallback(async () => {
    if (!mqttConnected) return;
    await startMedia();
    startBackgroundKeepAlive();
    publish('radio/webrtc/presence', {
      type: 'join' as const,
      peerId: peerIdRef.current,
      name: peerName,
      ts: Date.now(),
    });
  }, [mqttConnected, startMedia, startBackgroundKeepAlive, publish, peerName]);

  // PTT: enable/disable audio tracks
  const startTransmitting = useCallback(() => {
    if (!localStreamRef.current) return;
    localStreamRef.current.getAudioTracks().forEach(t => { t.enabled = true; });
    setIsTransmitting(true);
  }, []);

  const stopTransmitting = useCallback(() => {
    if (localStreamRef.current) {
      localStreamRef.current.getAudioTracks().forEach(t => { t.enabled = false; });
    }
    setIsTransmitting(false);
  }, []);

  // Set volume on all remote audio elements
  const setVolume = useCallback((vol: number) => {
    volumeRef.current = vol;
    peersRef.current.forEach(data => {
      if (data.audioEl) data.audioEl.volume = vol;
    });
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (mqttConnected) {
        publish('radio/webrtc/presence', {
          type: 'leave' as const,
          peerId: peerIdRef.current,
          ts: Date.now(),
        });
      }
      peersRef.current.forEach((data, id) => {
        if (data.audioEl) { data.audioEl.pause(); data.audioEl.remove(); }
        try { data.pc.close(); } catch { }
      });
      peersRef.current.clear();
      localStreamRef.current?.getTracks().forEach(t => t.stop());
      localStreamRef.current = null;
      bgCtxRef.current?.close();
      bgCtxRef.current = null;
    };
  }, []);

  return {
    connectedPeers,
    isTransmitting,
    localStream,
    joinChannel,
    startTransmitting,
    stopTransmitting,
    setVolume,
    handleMqttMessage,
  };
}
