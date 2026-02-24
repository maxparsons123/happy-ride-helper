import { useState, useRef, useCallback, useEffect, forwardRef, useImperativeHandle } from 'react';
import { Radio, Mic, MicOff, Volume2 } from 'lucide-react';
import { useWebRTCRadio } from '@/hooks/use-webrtc-radio';

export interface DriverRadioHandle {
  startPtt: () => void;
  stopPtt: () => void;
}

interface DriverRadioProps {
  driverId: string;
  driverName: string;
  publish: (topic: string, payload: any) => void;
  mqttConnected: boolean;
  lastRadioMessage?: any;
  remotePttState?: { from: string; name: string; active: boolean } | null;
  setWebRtcHandler: (handler: (topic: string, data: any) => boolean) => void;
}

interface RadioLogEntry {
  type: 'outgoing' | 'incoming';
  name: string;
  time: string;
}

export const DriverRadio = forwardRef<DriverRadioHandle, DriverRadioProps>(function DriverRadio(
  { driverId, driverName, publish, mqttConnected, remotePttState, setWebRtcHandler },
  ref
) {
  const [isOpen, setIsOpen] = useState(false);
  const [volume, setVolume] = useState(80);
  const [radioLog, setRadioLog] = useState<RadioLogEntry[]>([]);
  const logEndRef = useRef<HTMLDivElement>(null);

  const radio = useWebRTCRadio({
    peerId: driverId,
    peerName: driverName,
    publish,
    mqttConnected,
  });

  // Wire MQTT WebRTC messages to the radio hook
  useEffect(() => {
    setWebRtcHandler(radio.handleMqttMessage);
  }, [setWebRtcHandler, radio.handleMqttMessage]);

  const addLog = useCallback((type: RadioLogEntry['type'], name: string) => {
    const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    setRadioLog(prev => [...prev.slice(-19), { type, name, time }]);
  }, []);

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [radioLog]);

  // Auto-join radio channel when panel opens
  useEffect(() => {
    if (isOpen && mqttConnected) {
      radio.joinChannel();
    }
  }, [isOpen, mqttConnected]);

  const startPtt = useCallback(() => {
    if (!mqttConnected) return;
    radio.startTransmitting();
    publish('radio/ptt-state', { from: driverId, name: driverName, active: true, ts: Date.now() });
    addLog('outgoing', 'Dispatch');
  }, [mqttConnected, radio, addLog, publish, driverId, driverName]);

  const stopPtt = useCallback(() => {
    radio.stopTransmitting();
    publish('radio/ptt-state', { from: driverId, name: driverName, active: false, ts: Date.now() });
  }, [radio, publish, driverId, driverName]);

  const handleVolumeChange = useCallback((val: number) => {
    setVolume(val);
    radio.setVolume(val / 100);
  }, [radio]);

  const handleContextMenu = useCallback((e: React.MouseEvent) => e.preventDefault(), []);

  useImperativeHandle(ref, () => ({
    startPtt: () => { setIsOpen(true); startPtt(); },
    stopPtt,
  }), [startPtt, stopPtt]);

  const isReceiving = remotePttState?.active === true;

  if (!isOpen) {
    return (
      <button
        onClick={() => setIsOpen(true)}
        className={`fixed bottom-20 right-3 z-[900] w-11 h-11 rounded-full border-2 flex items-center justify-center transition-all shadow-lg ${
          isReceiving
            ? 'bg-red-600/30 border-red-500 text-red-400 animate-pulse shadow-[0_0_15px_rgba(239,68,68,0.4)]'
            : radio.connectedPeers.length > 0
              ? 'bg-green-600/20 border-green-500 text-green-400'
              : 'bg-cyan-600/20 border-cyan-500 text-cyan-400 hover:bg-cyan-600/30'
        }`}
        title={isReceiving ? `${remotePttState.name} is speaking` : 'Open Radio'}
      >
        <Radio className={`w-5 h-5 ${isReceiving ? 'animate-pulse' : ''}`} />
      </button>
    );
  }

  return (
    <div className={`fixed bottom-20 right-3 z-[900] w-56 bg-[#0a0a0a]/95 border rounded-2xl p-3 backdrop-blur-xl shadow-2xl transition-colors ${
      isReceiving ? 'border-red-500 shadow-[0_0_20px_rgba(239,68,68,0.3)]' : 'border-[#333]'
    }`}>
      {/* Header */}
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-1.5 text-cyan-400 text-xs font-bold">
          <Radio className="w-3.5 h-3.5" /> Radio
          {radio.connectedPeers.length > 0 && (
            <span className="text-[9px] text-green-400 font-mono">
              ({radio.connectedPeers.length} peer{radio.connectedPeers.length > 1 ? 's' : ''})
            </span>
          )}
        </div>
        <button onClick={() => setIsOpen(false)} className="text-gray-500 hover:text-white text-xs px-1">✕</button>
      </div>

      {/* Status */}
      <div className={`text-center text-[11px] mb-2 font-semibold ${
        isReceiving ? 'text-red-400' : radio.isTransmitting ? 'text-red-400' : mqttConnected ? 'text-gray-500' : 'text-yellow-500'
      }`}>
        {isReceiving
          ? `🔴 ${remotePttState.name} is speaking...`
          : radio.isTransmitting
            ? '🔴 TRANSMITTING...'
            : mqttConnected
              ? radio.connectedPeers.length > 0
                ? `🟢 ${radio.connectedPeers.length} peer(s) — Hold to talk`
                : 'Hold to talk'
              : 'Disconnected'}
      </div>

      {/* PTT Button */}
      <button
        className={`mx-auto block w-16 h-16 rounded-full border-[3px] transition-all select-none touch-none flex flex-col items-center justify-center gap-0.5 ${
          radio.isTransmitting
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
        {radio.isTransmitting ? <Mic className="w-5 h-5 animate-pulse" /> : <MicOff className="w-5 h-5" />}
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
          onChange={(e) => handleVolumeChange(Number(e.target.value))}
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
});
