import { useState, useRef, useCallback, useEffect } from 'react';
import { Radio, Mic, MicOff, Volume2, Users, User, Check } from 'lucide-react';
import type { OnlineDriver } from '@/hooks/use-mqtt-dispatch';

interface DispatchRadioProps {
  publish: (topic: string, payload: any) => void;
  mqttConnected: boolean;
  onlineDrivers: OnlineDriver[];
}

interface RadioLogEntry {
  type: 'outgoing' | 'incoming';
  name: string;
  time: string;
}

export function DispatchRadio({ publish, mqttConnected, onlineDrivers }: DispatchRadioProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [pttActive, setPttActive] = useState(false);
  const [volume, setVolume] = useState(80);
  const [radioLog, setRadioLog] = useState<RadioLogEntry[]>([]);
  const [selectedDrivers, setSelectedDrivers] = useState<Set<string>>(new Set()); // empty = all
  const [showDriverList, setShowDriverList] = useState(false);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const recorderRef = useRef<MediaRecorder | null>(null);
  const logEndRef = useRef<HTMLDivElement>(null);
  const pttActiveRef = useRef(false);

  const isAllMode = selectedDrivers.size === 0;

  const addLog = useCallback((type: RadioLogEntry['type'], name: string) => {
    const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    setRadioLog(prev => [...prev.slice(-19), { type, name, time }]);
  }, []);

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [radioLog]);

  const toggleDriver = useCallback((id: string) => {
    setSelectedDrivers(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const selectAll = useCallback(() => setSelectedDrivers(new Set()), []);

  const publishAudioChunk = useCallback((base64: string, mimeType: string) => {
    const payload = {
      driver: 'DISPATCH',
      name: 'Dispatch',
      audio: base64,
      mime: mimeType,
      ts: Date.now(),
    };

    if (selectedDrivers.size === 0) {
      // Broadcast to all
      publish('radio/broadcast', payload);
    } else {
      // Send to each selected driver's private radio topic
      selectedDrivers.forEach(dId => {
        publish(`radio/driver/${dId}`, payload);
      });
      // Also send on broadcast with a target list so drivers can filter
      publish('radio/broadcast', { ...payload, targets: Array.from(selectedDrivers) });
    }
  }, [publish, selectedDrivers]);

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

      const recorder = new MediaRecorder(mediaStreamRef.current, {
        mimeType,
        audioBitsPerSecond: 16000,
      });

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

      const targetLabel = isAllMode
        ? 'All Drivers'
        : selectedDrivers.size === 1
          ? onlineDrivers.find(d => selectedDrivers.has(d.id))?.name || 'Driver'
          : `${selectedDrivers.size} drivers`;
      addLog('outgoing', targetLabel);
    } catch {
      pttActiveRef.current = false;
      setPttActive(false);
    }
  }, [mqttConnected, publishAudioChunk, addLog, isAllMode, selectedDrivers, onlineDrivers]);

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
        className="fixed bottom-4 right-4 z-[900] w-12 h-12 rounded-full bg-cyan-600/20 border-2 border-cyan-500 text-cyan-400 flex items-center justify-center hover:bg-cyan-600/30 transition-colors shadow-lg"
        title="Open Radio Broadcast"
      >
        <Radio className="w-5 h-5" />
      </button>
    );
  }

  return (
    <div className="fixed bottom-4 right-4 z-[900] w-64 bg-[#0a0a0a]/95 border border-[#333] rounded-2xl p-4 backdrop-blur-xl shadow-2xl">
      {/* Header */}
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-2 text-cyan-400 text-sm font-bold">
          <Radio className="w-4 h-4" /> Radio
        </div>
        <button onClick={() => setIsOpen(false)} className="text-gray-500 hover:text-white text-sm px-1">✕</button>
      </div>

      {/* Target selector */}
      <div className="mb-3">
        <button
          onClick={() => setShowDriverList(!showDriverList)}
          className={`w-full text-left px-3 py-2 rounded-lg border text-xs font-semibold transition-colors ${
            isAllMode
              ? 'border-cyan-500/30 bg-cyan-500/10 text-cyan-400'
              : 'border-amber-500/30 bg-amber-500/10 text-amber-400'
          }`}
        >
          <div className="flex items-center justify-between">
            <span className="flex items-center gap-1.5">
              {isAllMode ? <Users className="w-3.5 h-3.5" /> : <User className="w-3.5 h-3.5" />}
              {isAllMode
                ? `All Drivers (${onlineDrivers.length} online)`
                : `${selectedDrivers.size} driver${selectedDrivers.size > 1 ? 's' : ''} selected`}
            </span>
            <span className="text-[10px] opacity-60">{showDriverList ? '▲' : '▼'}</span>
          </div>
        </button>

        {showDriverList && (
          <div className="mt-1 border border-[#333] rounded-lg bg-[#111] max-h-40 overflow-y-auto">
            {/* All drivers option */}
            <button
              onClick={selectAll}
              className={`w-full px-3 py-2 text-left text-xs flex items-center gap-2 hover:bg-white/5 transition-colors ${
                isAllMode ? 'text-cyan-400 font-bold' : 'text-gray-400'
              }`}
            >
              <div className={`w-4 h-4 rounded border flex items-center justify-center ${
                isAllMode ? 'bg-cyan-500 border-cyan-500' : 'border-gray-600'
              }`}>
                {isAllMode && <Check className="w-3 h-3 text-black" />}
              </div>
              <Users className="w-3 h-3" />
              All Drivers ({onlineDrivers.length})
            </button>

            {onlineDrivers.length === 0 && (
              <div className="px-3 py-3 text-center text-[11px] text-gray-600">
                No drivers online
              </div>
            )}

            {onlineDrivers.map(d => {
              const isSelected = selectedDrivers.has(d.id);
              const statusColor = d.status === 'available' ? 'bg-green-500' : d.status === 'busy' ? 'bg-amber-500' : 'bg-gray-500';
              return (
                <button
                  key={d.id}
                  onClick={() => toggleDriver(d.id)}
                  className={`w-full px-3 py-2 text-left text-xs flex items-center gap-2 hover:bg-white/5 transition-colors ${
                    isSelected ? 'text-amber-400' : 'text-gray-400'
                  }`}
                >
                  <div className={`w-4 h-4 rounded border flex items-center justify-center ${
                    isSelected ? 'bg-amber-500 border-amber-500' : 'border-gray-600'
                  }`}>
                    {isSelected && <Check className="w-3 h-3 text-black" />}
                  </div>
                  <span className={`w-2 h-2 rounded-full ${statusColor} flex-shrink-0`} />
                  <span className="truncate flex-1 font-semibold">{d.name}</span>
                  {d.registration && <span className="text-[9px] text-gray-600 font-mono">{d.registration}</span>}
                </button>
              );
            })}
          </div>
        )}
      </div>

      {/* Status */}
      <div className={`text-center text-xs mb-3 font-semibold ${
        pttActive ? 'text-red-400' : mqttConnected ? 'text-gray-500' : 'text-yellow-500'
      }`}>
        {pttActive
          ? isAllMode
            ? '🔴 BROADCASTING TO ALL...'
            : `🔴 TO ${selectedDrivers.size} DRIVER${selectedDrivers.size > 1 ? 'S' : ''}...`
          : mqttConnected
            ? 'Hold to broadcast'
            : 'MQTT disconnected'}
      </div>

      {/* PTT Button */}
      <button
        className={`mx-auto block w-20 h-20 rounded-full border-[3px] transition-all select-none touch-none flex flex-col items-center justify-center gap-1 ${
          pttActive
            ? 'border-red-500 bg-red-950/60 text-red-400 shadow-[0_0_20px_rgba(239,68,68,0.3)]'
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
        {pttActive ? <Mic className="w-6 h-6 animate-pulse" /> : <MicOff className="w-6 h-6" />}
        <span className="text-[10px] font-extrabold">PTT</span>
      </button>

      {/* Volume */}
      <div className="flex items-center gap-2 mt-3">
        <Volume2 className="w-3 h-3 text-gray-500" />
        <input
          type="range"
          min={0}
          max={100}
          value={volume}
          onChange={(e) => setVolume(Number(e.target.value))}
          className="flex-1 h-1 bg-[#333] rounded appearance-none [&::-webkit-slider-thumb]:appearance-none [&::-webkit-slider-thumb]:w-3 [&::-webkit-slider-thumb]:h-3 [&::-webkit-slider-thumb]:bg-cyan-400 [&::-webkit-slider-thumb]:rounded-full [&::-webkit-slider-thumb]:cursor-pointer"
        />
      </div>

      {/* Log */}
      {radioLog.length > 0 && (
        <div className="mt-3 max-h-24 overflow-y-auto text-[11px] space-y-1">
          {radioLog.map((entry, i) => (
            <div
              key={i}
              className={`px-2 py-1 rounded flex items-center justify-between ${
                entry.type === 'outgoing' ? 'bg-cyan-500/10 text-cyan-400' : 'bg-green-500/10 text-green-400'
              }`}
            >
              <span className="font-bold">{entry.name}</span>
              <span className="text-gray-600 text-[9px]">{entry.time}</span>
            </div>
          ))}
          <div ref={logEndRef} />
        </div>
      )}
    </div>
  );
}
