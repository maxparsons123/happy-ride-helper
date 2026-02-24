import { useState, useRef, useCallback, useEffect } from 'react';
import { Radio, Mic, MicOff, Volume2 } from 'lucide-react';
import type { RadioMessage } from '@/hooks/use-mqtt-driver';

interface DriverRadioProps {
  driverId: string;
  driverName: string;
  publish: (topic: string, payload: any) => void;
  mqttConnected: boolean;
  lastRadioMessage: RadioMessage | null;
}

interface RadioLogEntry {
  type: 'outgoing' | 'incoming';
  name: string;
  time: string;
}

export function DriverRadio({ driverId, driverName, publish, mqttConnected, lastRadioMessage }: DriverRadioProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [pttActive, setPttActive] = useState(false);
  const [receiving, setReceiving] = useState(false);
  const [volume, setVolume] = useState(80);
  const [radioLog, setRadioLog] = useState<RadioLogEntry[]>([]);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const recorderRef = useRef<MediaRecorder | null>(null);
  const logEndRef = useRef<HTMLDivElement>(null);
  const pttActiveRef = useRef(false);
  const audioCtxRef = useRef<AudioContext | null>(null);
  const gainRef = useRef<GainNode | null>(null);
  const processedTsRef = useRef<Set<number>>(new Set());

  const addLog = useCallback((type: RadioLogEntry['type'], name: string) => {
    const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    setRadioLog(prev => [...prev.slice(-19), { type, name, time }]);
  }, []);

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [radioLog]);

  // Update gain when volume changes
  useEffect(() => {
    if (gainRef.current) gainRef.current.gain.value = volume / 100;
  }, [volume]);

  // Play incoming radio audio
  useEffect(() => {
    if (!lastRadioMessage || !lastRadioMessage.audio) return;
    if (processedTsRef.current.has(lastRadioMessage.ts)) return;
    processedTsRef.current.add(lastRadioMessage.ts);
    // Keep set small
    if (processedTsRef.current.size > 50) {
      const arr = Array.from(processedTsRef.current);
      processedTsRef.current = new Set(arr.slice(-30));
    }

    const playAudio = async () => {
      try {
        if (!audioCtxRef.current) {
          audioCtxRef.current = new AudioContext();
          gainRef.current = audioCtxRef.current.createGain();
          gainRef.current.gain.value = volume / 100;
          gainRef.current.connect(audioCtxRef.current.destination);
        }
        const ctx = audioCtxRef.current;
        if (ctx.state === 'suspended') await ctx.resume();

        const binary = atob(lastRadioMessage.audio);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

        const blob = new Blob([bytes], { type: lastRadioMessage.mime || 'audio/webm;codecs=opus' });
        const arrayBuf = await blob.arrayBuffer();
        const audioBuf = await ctx.decodeAudioData(arrayBuf);

        const source = ctx.createBufferSource();
        source.buffer = audioBuf;
        source.connect(gainRef.current!);
        setReceiving(true);
        source.onended = () => setReceiving(false);
        source.start();

        addLog('incoming', lastRadioMessage.name || 'Dispatch');
      } catch {
        setReceiving(false);
      }
    };
    playAudio();
  }, [lastRadioMessage, volume, addLog]);

  const publishAudioChunk = useCallback((base64: string, mimeType: string) => {
    publish('radio/channel', {
      driver: driverId,
      name: driverName,
      audio: base64,
      mime: mimeType,
      ts: Date.now(),
    });
  }, [publish, driverId, driverName]);

  const startPtt = useCallback(async () => {
    if (pttActiveRef.current || !mqttConnected) return;
    pttActiveRef.current = true;
    setPttActive(true);

    try {
      if (!mediaStreamRef.current) {
        mediaStreamRef.current = await navigator.mediaDevices.getUserMedia({
          audio: { echoCancellation: true, noiseSuppression: true, sampleRate: 16000 },
          video: false,
        });
      }

      let mimeType = 'audio/webm;codecs=opus';
      if (!MediaRecorder.isTypeSupported(mimeType)) {
        mimeType = 'audio/webm';
        if (!MediaRecorder.isTypeSupported(mimeType)) mimeType = 'audio/ogg;codecs=opus';
      }

      const recorder = new MediaRecorder(mediaStreamRef.current, { mimeType, audioBitsPerSecond: 16000 });
      recorder.ondataavailable = (e) => {
        if (e.data.size > 0 && pttActiveRef.current) {
          const reader = new FileReader();
          reader.onloadend = () => {
            const base64 = (reader.result as string)?.split(',')[1];
            if (base64) publishAudioChunk(base64, mimeType);
          };
          reader.readAsDataURL(e.data);
        }
      };

      recorder.start(500);
      recorderRef.current = recorder;
      addLog('outgoing', 'Dispatch');
    } catch {
      pttActiveRef.current = false;
      setPttActive(false);
    }
  }, [mqttConnected, publishAudioChunk, addLog]);

  const stopPtt = useCallback(() => {
    if (!pttActiveRef.current) return;
    pttActiveRef.current = false;
    setPttActive(false);
    if (recorderRef.current && recorderRef.current.state !== 'inactive') {
      recorderRef.current.stop();
    }
    recorderRef.current = null;
  }, []);

  const handleContextMenu = useCallback((e: React.MouseEvent) => e.preventDefault(), []);

  if (!isOpen) {
    return (
      <button
        onClick={() => setIsOpen(true)}
        className={`fixed bottom-20 right-3 z-[900] w-11 h-11 rounded-full border-2 flex items-center justify-center transition-colors shadow-lg ${
          receiving
            ? 'bg-green-600/30 border-green-500 text-green-400 animate-pulse'
            : 'bg-cyan-600/20 border-cyan-500 text-cyan-400 hover:bg-cyan-600/30'
        }`}
        title="Open Radio"
      >
        <Radio className="w-5 h-5" />
      </button>
    );
  }

  return (
    <div className="fixed bottom-20 right-3 z-[900] w-56 bg-[#0a0a0a]/95 border border-[#333] rounded-2xl p-3 backdrop-blur-xl shadow-2xl">
      {/* Header */}
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-1.5 text-cyan-400 text-xs font-bold">
          <Radio className="w-3.5 h-3.5" /> Radio
        </div>
        <button onClick={() => setIsOpen(false)} className="text-gray-500 hover:text-white text-xs px-1">✕</button>
      </div>

      {/* Status */}
      <div className={`text-center text-[11px] mb-2 font-semibold ${
        pttActive ? 'text-red-400' : receiving ? 'text-green-400' : mqttConnected ? 'text-gray-500' : 'text-yellow-500'
      }`}>
        {pttActive
          ? '🔴 TRANSMITTING...'
          : receiving
            ? '🟢 RECEIVING...'
            : mqttConnected
              ? 'Hold to talk'
              : 'Disconnected'}
      </div>

      {/* PTT Button */}
      <button
        className={`mx-auto block w-16 h-16 rounded-full border-[3px] transition-all select-none touch-none flex flex-col items-center justify-center gap-0.5 ${
          pttActive
            ? 'border-red-500 bg-red-950/60 text-red-400 shadow-[0_0_20px_rgba(239,68,68,0.3)]'
            : receiving
              ? 'border-green-500 bg-green-950/40 text-green-400'
              : 'border-[#444] bg-gradient-to-b from-[#222] to-[#1a1a1a] text-gray-500 hover:border-[#666]'
        }`}
        onMouseDown={startPtt}
        onMouseUp={stopPtt}
        onMouseLeave={stopPtt}
        onTouchStart={(e) => { e.preventDefault(); startPtt(); }}
        onTouchEnd={(e) => { e.preventDefault(); stopPtt(); }}
        onTouchCancel={stopPtt}
        onContextMenu={handleContextMenu}
        disabled={!mqttConnected}
      >
        {pttActive ? <Mic className="w-5 h-5 animate-pulse" /> : <MicOff className="w-5 h-5" />}
        <span className="text-[9px] font-extrabold">PTT</span>
      </button>

      {/* Volume */}
      <div className="flex items-center gap-1.5 mt-2">
        <Volume2 className="w-3 h-3 text-gray-500" />
        <input
          type="range"
          min={0}
          max={100}
          value={volume}
          onChange={(e) => setVolume(Number(e.target.value))}
          className="flex-1 h-1 bg-[#333] rounded appearance-none [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-2.5 [&::-webkit-slider-thumb]:h-2.5 [&::-webkit-slider-thumb]:bg-cyan-400 [&::-webkit-slider-thumb]:rounded-full [&::-webkit-slider-thumb]:cursor-pointer"
        />
      </div>

      {/* Log */}
      {radioLog.length > 0 && (
        <div className="mt-2 max-h-20 overflow-y-auto text-[10px] space-y-0.5">
          {radioLog.map((entry, i) => (
            <div
              key={i}
              className={`px-1.5 py-0.5 rounded flex items-center justify-between ${
                entry.type === 'outgoing' ? 'bg-cyan-500/10 text-cyan-400' : 'bg-green-500/10 text-green-400'
              }`}
            >
              <span className="font-bold">{entry.type === 'outgoing' ? '→' : '←'} {entry.name}</span>
              <span className="text-gray-600 text-[8px]">{entry.time}</span>
            </div>
          ))}
          <div ref={logEndRef} />
        </div>
      )}
    </div>
  );
}
