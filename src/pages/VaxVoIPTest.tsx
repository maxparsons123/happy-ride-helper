import { useState, useRef, useEffect, useCallback } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Mic, Phone, PhoneOff, Settings, Server, Activity, Copy, Check } from "lucide-react";
import { supabase } from "@/integrations/supabase/client";
import { Link } from "react-router-dom";

interface Message {
  text: string;
  type: "user" | "assistant" | "system" | "tool";
  latency?: number;
  timestamp: Date;
}

interface Booking {
  pickup: string;
  destination: string;
  passengers: number;
  fare: string;
  eta: string;
}

interface VaxConfig {
  sipPort: number;
  domain: string;
  maxCalls: number;
  enableRecording: boolean;
  recordingsPath: string;
  rtpPortMin: number;
  rtpPortMax: number;
  openaiModel: string;
  voice: string;
  autoAnswer: boolean;
  companyName: string;
}

const DEFAULT_CONFIG: VaxConfig = {
  sipPort: 5060,
  domain: "taxi.local",
  maxCalls: 50,
  enableRecording: true,
  recordingsPath: "C:\\TaxiRecordings\\",
  rtpPortMin: 10000,
  rtpPortMax: 20000,
  openaiModel: "gpt-4o-realtime-preview-2024-10-01",
  voice: "shimmer",
  autoAnswer: true,
  companyName: "Ada Taxi",
};

export default function VaxVoIPTest() {
  // Connection state
  const [status, setStatus] = useState<"disconnected" | "connecting" | "connected">("disconnected");
  const [messages, setMessages] = useState<Message[]>([]);
  const [booking, setBooking] = useState<Booking | null>(null);
  const [textInput, setTextInput] = useState("");
  const [isSpeaking, setIsSpeaking] = useState(false);
  const [isRecording, setIsRecording] = useState(false);
  const [voiceStatus, setVoiceStatus] = useState("Connect first...");

  // Config state
  const [config, setConfig] = useState<VaxConfig>(DEFAULT_CONFIG);
  const [showConfig, setShowConfig] = useState(true);
  const [copied, setCopied] = useState(false);

  // Simulated call stats
  const [activeCalls, setActiveCalls] = useState(0);
  const [totalCalls, setTotalCalls] = useState(0);
  const [uptime, setUptime] = useState(0);

  // Metrics
  const [lastLatency, setLastLatency] = useState<number | null>(null);
  const [avgLatency, setAvgLatency] = useState<number | null>(null);
  const [firstAudioLatency, setFirstAudioLatency] = useState<number | null>(null);
  const [responseCount, setResponseCount] = useState(0);

  // Audio refs
  const wsRef = useRef<WebSocket | null>(null);
  const audioContextRef = useRef<AudioContext | null>(null);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const workletNodeRef = useRef<AudioWorkletNode | null>(null);
  const silentGainRef = useRef<GainNode | null>(null);
  const currentTranscriptRef = useRef("");
  const speechStartTimeRef = useRef(0);
  const firstAudioTimeRef = useRef(0);
  const latenciesRef = useRef<number[]>([]);
  const nextStartTimeRef = useRef(0);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const isConnectingRef = useRef(false);
  const audioSentRef = useRef(false);
  const uptimeIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Base64 helper
  const BASE64_DECODER = new TextDecoder("latin1");
  const uint8ToBase64 = (bytes: Uint8Array) => btoa(BASE64_DECODER.decode(bytes));

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages, booking]);

  // Uptime counter
  useEffect(() => {
    if (status === "connected") {
      const start = Date.now();
      uptimeIntervalRef.current = setInterval(() => {
        setUptime(Math.floor((Date.now() - start) / 1000));
      }, 1000);
    } else {
      if (uptimeIntervalRef.current) {
        clearInterval(uptimeIntervalRef.current);
        uptimeIntervalRef.current = null;
      }
      setUptime(0);
    }
    return () => {
      if (uptimeIntervalRef.current) clearInterval(uptimeIntervalRef.current);
    };
  }, [status]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      cleanupAudio();
      wsRef.current?.close();
    };
  }, []);

  // Keyboard: Space to talk
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.code === "Space" && !e.repeat && status === "connected" && !isRecording) {
        e.preventDefault();
        startRecording();
      }
    };
    const handleKeyUp = (e: KeyboardEvent) => {
      if (e.code === "Space" && isRecording) {
        e.preventDefault();
        stopRecording();
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    window.addEventListener("keyup", handleKeyUp);
    return () => {
      window.removeEventListener("keydown", handleKeyDown);
      window.removeEventListener("keyup", handleKeyUp);
    };
  }, [status, isRecording]);

  const cleanupAudio = useCallback(() => {
    if (workletNodeRef.current) {
      workletNodeRef.current.disconnect();
      workletNodeRef.current = null;
    }
    if (silentGainRef.current) {
      silentGainRef.current.disconnect();
      silentGainRef.current = null;
    }
    if (mediaStreamRef.current) {
      mediaStreamRef.current.getTracks().forEach(t => t.stop());
      mediaStreamRef.current = null;
    }
    if (audioContextRef.current && audioContextRef.current.state !== "closed") {
      audioContextRef.current.close().catch(() => {});
    }
    audioContextRef.current = null;
    nextStartTimeRef.current = 0;
  }, []);

  const addMessage = useCallback((text: string, type: Message["type"], latency?: number) => {
    setMessages(prev => [...prev, { text, type, latency, timestamp: new Date() }]);
  }, []);

  const updateMetrics = useCallback((latency: number) => {
    latenciesRef.current.push(latency);
    setLastLatency(latency);
    setAvgLatency(Math.round(latenciesRef.current.reduce((a, b) => a + b, 0) / latenciesRef.current.length));
    setResponseCount(latenciesRef.current.length);
  }, []);

  const playAudioChunk = useCallback((base64Audio: string) => {
    if (!audioContextRef.current || audioContextRef.current.state === "closed") {
      audioContextRef.current = new AudioContext({ sampleRate: 24000 });
      nextStartTimeRef.current = 0;
    }
    const ctx = audioContextRef.current;
    if (ctx.state === "suspended") ctx.resume();

    try {
      const binary = atob(base64Audio);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

      const int16 = new Int16Array(bytes.buffer);
      const float32 = new Float32Array(int16.length);
      for (let i = 0; i < int16.length; i++) float32[i] = int16[i] / 32768.0;

      const audioBuffer = ctx.createBuffer(1, float32.length, 24000);
      audioBuffer.getChannelData(0).set(float32);

      const source = ctx.createBufferSource();
      source.buffer = audioBuffer;
      source.connect(ctx.destination);

      const now = ctx.currentTime;
      if (nextStartTimeRef.current < now) nextStartTimeRef.current = now + 0.05;
      source.start(nextStartTimeRef.current);
      nextStartTimeRef.current += audioBuffer.duration;
    } catch (error) {
      console.error("Error playing audio:", error);
    }
  }, []);

  const connect = useCallback(async () => {
    if (isConnectingRef.current || wsRef.current?.readyState === WebSocket.OPEN) return;

    isConnectingRef.current = true;
    setStatus("connecting");
    addMessage("Initializing VaxVoIP bridge simulation...", "system");
    addMessage(`SIP Server: ${config.domain}:${config.sipPort} | Max calls: ${config.maxCalls}`, "system");

    cleanupAudio();
    audioContextRef.current = new AudioContext({ sampleRate: 24000 });
    if (audioContextRef.current.state === "suspended") {
      await audioContextRef.current.resume();
    }

    // Connect to the paired backend (same as VoiceTest)
    const projectId = import.meta.env.VITE_SUPABASE_PROJECT_ID || "";
    const wsUrl = `wss://${projectId}.functions.supabase.co/functions/v1/taxi-realtime-paired`;
    const ws = new WebSocket(wsUrl);
    wsRef.current = ws;

    ws.onopen = () => {
      addMessage("WebSocket connected, sending init...", "system");
      ws.send(JSON.stringify({
        type: "init",
        call_id: "vaxvoip-test-" + Date.now(),
        addressTtsSplicing: true,
        agent: "ada",
        voice: config.voice,
        useUnifiedExtraction: false,
        // VaxVoIP metadata
        bridge: "vaxvoip",
        sipDomain: config.domain,
        sipPort: config.sipPort,
      }));
    };

    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);

      if (data?.type === "keepalive") {
        try { ws.send(JSON.stringify({ type: "keepalive_ack", timestamp: data.timestamp, call_id: data.call_id })); } catch {}
        return;
      }

      if (data.type === "session_ready") {
        setStatus("connected");
        isConnectingRef.current = false;
        setActiveCalls(1);
        setTotalCalls(prev => prev + 1);
        addMessage("✅ VaxVoIP session ready! Hold mic or press Space to speak.", "system");
        setVoiceStatus("Ready - hold to speak (or Space)");
      }

      if (data.type === "ai_speaking") setIsSpeaking(Boolean(data.speaking));

      if (data.type === "audio" && speechStartTimeRef.current > 0 && firstAudioTimeRef.current === 0) {
        firstAudioTimeRef.current = Date.now();
        const latency = firstAudioTimeRef.current - speechStartTimeRef.current;
        setFirstAudioLatency(latency);
        updateMetrics(latency);
      }

      if (data.type === "audio") {
        setIsSpeaking(true);
        playAudioChunk(data.audio);
      }

      if (data.type === "address_tts") playAudioChunk(data.audio);

      if (data.type === "transcript") {
        if (data.role === "assistant") {
          currentTranscriptRef.current += data.text;
        } else if (data.role === "user") {
          addMessage(data.text, "user");
        }
      }

      if (data.type === "response_done") {
        setIsSpeaking(false);
        if (currentTranscriptRef.current) {
          const responseLatency = speechStartTimeRef.current > 0 ? Date.now() - speechStartTimeRef.current : undefined;
          addMessage(currentTranscriptRef.current, "assistant", responseLatency);
          currentTranscriptRef.current = "";
          speechStartTimeRef.current = 0;
          firstAudioTimeRef.current = 0;
        }
      }

      if (data.type === "tool_call" || data.type === "function_call") {
        addMessage(`🔧 Tool: ${data.name || data.function || "unknown"}`, "tool");
      }

      if (data.type === "booking_confirmed") setBooking(data.booking);

      if (data.type === "error") addMessage("Error: " + JSON.stringify(data.error), "system");
    };

    ws.onclose = () => {
      setStatus("disconnected");
      setIsSpeaking(false);
      setIsRecording(false);
      isConnectingRef.current = false;
      wsRef.current = null;
      setActiveCalls(0);
      addMessage("Disconnected", "system");
      setVoiceStatus("Connect first...");
      cleanupAudio();
    };

    ws.onerror = () => {
      isConnectingRef.current = false;
      addMessage("Connection error", "system");
    };
  }, [addMessage, playAudioChunk, updateMetrics, cleanupAudio, config]);

  const disconnect = useCallback(() => {
    cleanupAudio();
    wsRef.current?.close();
    wsRef.current = null;
    isConnectingRef.current = false;
    setIsSpeaking(false);
    setIsRecording(false);
  }, [cleanupAudio]);

  const startRecording = useCallback(async () => {
    if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
    if (mediaStreamRef.current) return;

    setIsRecording(true);
    setVoiceStatus("🔴 Recording...");
    speechStartTimeRef.current = Date.now();
    firstAudioTimeRef.current = 0;
    audioSentRef.current = false;

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: { sampleRate: 24000, channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }
      });
      mediaStreamRef.current = stream;

      let ctx = audioContextRef.current;
      if (!ctx || ctx.state === "closed") {
        ctx = new AudioContext({ sampleRate: 24000 });
        audioContextRef.current = ctx;
        nextStartTimeRef.current = 0;
      }
      if (ctx.state === "suspended") await ctx.resume();

      try { await ctx.audioWorklet.addModule("/audio-worklet.js"); } catch {}

      const source = ctx.createMediaStreamSource(stream);
      const workletNode = new AudioWorkletNode(ctx, "recorder");
      workletNodeRef.current = workletNode;

      workletNode.port.onmessage = (e) => {
        const ws = wsRef.current;
        if (!ws || ws.readyState !== WebSocket.OPEN) return;
        if (ws.bufferedAmount > 2_000_000) return;

        const buffer = e.data as ArrayBuffer;
        const uint8 = new Uint8Array(buffer);
        const base64 = uint8ToBase64(uint8);
        ws.send(JSON.stringify({ type: "audio", audio: base64 }));
        audioSentRef.current = true;
      };

      source.connect(workletNode);
      const silentGain = ctx.createGain();
      silentGain.gain.value = 0;
      silentGainRef.current = silentGain;
      workletNode.connect(silentGain);
      silentGain.connect(ctx.destination);
    } catch (error) {
      addMessage("Microphone access denied: " + (error as Error).message, "system");
      setIsRecording(false);
      setVoiceStatus("Ready - hold to speak (or Space)");
    }
  }, [addMessage]);

  const stopRecording = useCallback(() => {
    if (!mediaStreamRef.current) return;
    setIsRecording(false);
    setVoiceStatus("Processing...");

    if (workletNodeRef.current) { workletNodeRef.current.disconnect(); workletNodeRef.current = null; }
    if (silentGainRef.current) { silentGainRef.current.disconnect(); silentGainRef.current = null; }
    if (mediaStreamRef.current) { mediaStreamRef.current.getTracks().forEach(t => t.stop()); mediaStreamRef.current = null; }

    if (wsRef.current?.readyState === WebSocket.OPEN && audioSentRef.current) {
      wsRef.current.send(JSON.stringify({ type: "commit" }));
    }
    audioSentRef.current = false;

    setTimeout(() => {
      if (wsRef.current?.readyState === WebSocket.OPEN) setVoiceStatus("Ready - hold to speak (or Space)");
    }, 500);
  }, []);

  const sendTextMessage = useCallback(() => {
    if (!textInput.trim() || !wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
    speechStartTimeRef.current = Date.now();
    firstAudioTimeRef.current = 0;
    wsRef.current.send(JSON.stringify({ type: "text", text: textInput }));
    addMessage(textInput, "user");
    setTextInput("");
  }, [textInput, addMessage]);

  const copyConfig = () => {
    const configJson = JSON.stringify({
      vaxVoIP: {
        licenseKey: "YOUR_LICENSE_KEY",
        domainRealm: config.domain,
        sipPort: config.sipPort,
        rtpPortMin: config.rtpPortMin,
        rtpPortMax: config.rtpPortMax,
        enableRecording: config.enableRecording,
        recordingsPath: config.recordingsPath,
        maxConcurrentCalls: config.maxCalls,
      },
      openAI: {
        apiKey: "YOUR_OPENAI_KEY",
        model: config.openaiModel,
        voice: config.voice,
      },
      taxi: {
        companyName: config.companyName,
        autoAnswer: config.autoAnswer,
      }
    }, null, 2);

    navigator.clipboard.writeText(configJson);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const formatUptime = (s: number) => {
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return `${m}:${sec.toString().padStart(2, "0")}`;
  };

  const prefersReducedMotion = typeof window !== "undefined"
    && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  return (
    <div className="min-h-screen bg-gradient-dark p-6">
      <div className="max-w-3xl mx-auto space-y-4">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <Link to="/voice-test" className="text-muted-foreground hover:text-foreground text-sm">
              ← Voice Test
            </Link>
            <h1 className="text-2xl font-bold text-primary">🖥️ VaxVoIP Bridge Test</h1>
          </div>
          <div className={`px-4 py-2 rounded-full text-sm font-medium ${
            status === "connected" ? "bg-green-600" :
            status === "connecting" ? "bg-orange-500" : "bg-red-600"
          }`}>
            {status.charAt(0).toUpperCase() + status.slice(1)}
          </div>
        </div>

        {/* Server Status Bar */}
        <div className="grid grid-cols-4 gap-3">
          <div className="bg-card rounded-lg border border-border p-3 text-center">
            <Server className="w-4 h-4 mx-auto text-muted-foreground mb-1" />
            <p className="text-lg font-mono font-bold text-foreground">{activeCalls}</p>
            <p className="text-xs text-muted-foreground">Active Calls</p>
          </div>
          <div className="bg-card rounded-lg border border-border p-3 text-center">
            <Activity className="w-4 h-4 mx-auto text-muted-foreground mb-1" />
            <p className="text-lg font-mono font-bold text-foreground">{totalCalls}</p>
            <p className="text-xs text-muted-foreground">Total Calls</p>
          </div>
          <div className="bg-card rounded-lg border border-border p-3 text-center">
            <p className="text-lg font-mono font-bold text-foreground">{formatUptime(uptime)}</p>
            <p className="text-xs text-muted-foreground">Uptime</p>
          </div>
          <div className="bg-card rounded-lg border border-border p-3 text-center">
            <p className="text-lg font-mono font-bold text-green-400">{lastLatency ?? "--"}ms</p>
            <p className="text-xs text-muted-foreground">Latency</p>
          </div>
        </div>

        {/* Config Panel */}
        <div className="bg-card rounded-xl border border-border">
          <button
            onClick={() => setShowConfig(!showConfig)}
            className="w-full flex items-center justify-between p-4"
          >
            <div className="flex items-center gap-2">
              <Settings className="w-4 h-4 text-primary" />
              <span className="font-semibold text-primary">VaxVoIP Configuration</span>
            </div>
            <span className="text-muted-foreground text-sm">{showConfig ? "▲" : "▼"}</span>
          </button>

          {showConfig && (
            <div className="px-4 pb-4 space-y-4">
              {/* SIP Settings */}
              <div>
                <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider mb-2">SIP Server</h4>
                <div className="grid grid-cols-3 gap-3">
                  <div>
                    <label className="text-xs text-muted-foreground">Domain</label>
                    <Input
                      value={config.domain}
                      onChange={e => setConfig(c => ({ ...c, domain: e.target.value }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 font-mono text-sm"
                    />
                  </div>
                  <div>
                    <label className="text-xs text-muted-foreground">SIP Port</label>
                    <Input
                      type="number"
                      value={config.sipPort}
                      onChange={e => setConfig(c => ({ ...c, sipPort: parseInt(e.target.value) || 5060 }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 font-mono text-sm"
                    />
                  </div>
                  <div>
                    <label className="text-xs text-muted-foreground">Max Calls</label>
                    <Input
                      type="number"
                      value={config.maxCalls}
                      onChange={e => setConfig(c => ({ ...c, maxCalls: parseInt(e.target.value) || 50 }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 font-mono text-sm"
                    />
                  </div>
                </div>
              </div>

              {/* RTP & Recording */}
              <div>
                <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider mb-2">RTP & Recording</h4>
                <div className="grid grid-cols-3 gap-3">
                  <div>
                    <label className="text-xs text-muted-foreground">RTP Min Port</label>
                    <Input
                      type="number"
                      value={config.rtpPortMin}
                      onChange={e => setConfig(c => ({ ...c, rtpPortMin: parseInt(e.target.value) || 10000 }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 font-mono text-sm"
                    />
                  </div>
                  <div>
                    <label className="text-xs text-muted-foreground">RTP Max Port</label>
                    <Input
                      type="number"
                      value={config.rtpPortMax}
                      onChange={e => setConfig(c => ({ ...c, rtpPortMax: parseInt(e.target.value) || 20000 }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 font-mono text-sm"
                    />
                  </div>
                  <div>
                    <label className="text-xs text-muted-foreground">Recordings Path</label>
                    <Input
                      value={config.recordingsPath}
                      onChange={e => setConfig(c => ({ ...c, recordingsPath: e.target.value }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 font-mono text-sm"
                    />
                  </div>
                </div>
                <label className="flex items-center gap-2 mt-2 text-sm">
                  <input
                    type="checkbox"
                    checked={config.enableRecording}
                    onChange={e => setConfig(c => ({ ...c, enableRecording: e.target.checked }))}
                    disabled={status !== "disconnected"}
                    className="rounded border-muted-foreground"
                  />
                  <span className="text-muted-foreground">Enable call recording</span>
                </label>
              </div>

              {/* AI Settings */}
              <div>
                <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider mb-2">OpenAI Realtime</h4>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="text-xs text-muted-foreground">Model</label>
                    <Select
                      value={config.openaiModel}
                      onValueChange={v => setConfig(c => ({ ...c, openaiModel: v }))}
                      disabled={status !== "disconnected"}
                    >
                      <SelectTrigger className="mt-1 bg-input border-border font-mono text-sm">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent className="bg-card border-border">
                        <SelectItem value="gpt-4o-realtime-preview-2024-10-01">gpt-4o-realtime-preview</SelectItem>
                        <SelectItem value="gpt-4o-mini-realtime-preview">gpt-4o-mini-realtime</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                  <div>
                    <label className="text-xs text-muted-foreground">Voice</label>
                    <Select
                      value={config.voice}
                      onValueChange={v => setConfig(c => ({ ...c, voice: v }))}
                      disabled={status !== "disconnected"}
                    >
                      <SelectTrigger className="mt-1 bg-input border-border font-mono text-sm">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent className="bg-card border-border">
                        {["shimmer", "alloy", "echo", "fable", "onyx", "nova", "ash", "coral", "sage", "verse"].map(v => (
                          <SelectItem key={v} value={v}>{v.charAt(0).toUpperCase() + v.slice(1)}</SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                </div>
              </div>

              {/* Taxi Settings */}
              <div>
                <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider mb-2">Taxi Booking</h4>
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="text-xs text-muted-foreground">Company Name</label>
                    <Input
                      value={config.companyName}
                      onChange={e => setConfig(c => ({ ...c, companyName: e.target.value }))}
                      disabled={status !== "disconnected"}
                      className="mt-1 text-sm"
                    />
                  </div>
                  <div className="flex items-end">
                    <label className="flex items-center gap-2 text-sm pb-2">
                      <input
                        type="checkbox"
                        checked={config.autoAnswer}
                        onChange={e => setConfig(c => ({ ...c, autoAnswer: e.target.checked }))}
                        disabled={status !== "disconnected"}
                        className="rounded border-muted-foreground"
                      />
                      <span className="text-muted-foreground">Auto-answer calls</span>
                    </label>
                  </div>
                </div>
              </div>

              {/* Copy Config */}
              <Button onClick={copyConfig} variant="outline" size="sm" className="w-full">
                {copied ? <Check className="w-4 h-4 mr-2" /> : <Copy className="w-4 h-4 mr-2" />}
                {copied ? "Copied!" : "Copy Config JSON (for C# appsettings.json)"}
              </Button>
            </div>
          )}
        </div>

        {/* Connect Controls */}
        <div className="flex gap-3">
          <Button
            onClick={connect}
            disabled={status === "connected" || status === "connecting"}
            className="bg-gradient-gold flex-1"
          >
            <Phone className="w-4 h-4 mr-2" />
            Simulate Incoming Call
          </Button>
          <Button onClick={disconnect} variant="outline" disabled={status === "disconnected"}>
            <PhoneOff className="w-4 h-4 mr-2" />
            Hang Up
          </Button>
        </div>

        {/* Voice Area */}
        <div className="flex items-center gap-4 p-4 bg-card rounded-xl border border-border">
          <button
            onMouseDown={startRecording}
            onMouseUp={stopRecording}
            onMouseLeave={() => isRecording && stopRecording()}
            onTouchStart={(e) => { e.preventDefault(); startRecording(); }}
            onTouchEnd={(e) => { e.preventDefault(); stopRecording(); }}
            disabled={status !== "connected"}
            style={{ touchAction: "none" }}
            className={`w-16 h-16 rounded-full flex items-center justify-center transition-all select-none focus:outline-none focus:ring-2 focus:ring-primary focus:ring-offset-2 focus:ring-offset-background ${
              isRecording
                ? `bg-green-500 ${prefersReducedMotion ? "" : "animate-pulse"}`
                : status === "connected"
                  ? "bg-red-500 hover:bg-red-600"
                  : "bg-muted cursor-not-allowed"
            }`}
          >
            <Mic className="w-6 h-6 text-foreground" />
          </button>

          <div className="flex-1">
            <p className="font-semibold">Hold to speak</p>
            <p className="text-sm text-muted-foreground">{voiceStatus}</p>
            <p className="text-xs text-muted-foreground mt-1">Or press Space on keyboard</p>
          </div>

          {isSpeaking && (
            <div className="flex items-center gap-2 px-3 py-2 bg-primary/20 rounded-full">
              <div className="flex items-center gap-1">
                {prefersReducedMotion ? (
                  <span className="w-2 h-2 bg-primary rounded-full" />
                ) : (
                  <>
                    <span className="w-1 h-3 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_infinite]" />
                    <span className="w-1 h-4 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.1s_infinite]" />
                    <span className="w-1 h-2 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.2s_infinite]" />
                    <span className="w-1 h-5 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.3s_infinite]" />
                    <span className="w-1 h-3 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.4s_infinite]" />
                  </>
                )}
              </div>
              <span className="text-sm font-medium text-primary">AI Speaking</span>
            </div>
          )}
        </div>

        {/* Messages */}
        <div className="h-80 overflow-y-auto bg-card rounded-xl border border-border p-4 space-y-3">
          {messages.map((msg, i) => (
            <div
              key={i}
              className={`max-w-[85%] p-3 rounded-xl text-sm ${
                msg.type === "user"
                  ? "ml-auto bg-primary text-primary-foreground"
                  : msg.type === "assistant"
                    ? "bg-muted"
                    : msg.type === "tool"
                      ? "mx-auto text-center text-xs text-accent bg-accent/10 border border-accent/20"
                      : "mx-auto text-center text-xs text-muted-foreground bg-muted/50"
              }`}
            >
              {msg.text}
              {msg.latency && (
                <div className="text-xs mt-1 text-green-400">⚡ {msg.latency}ms</div>
              )}
            </div>
          ))}
          {booking && (
            <div className="bg-green-600 p-4 rounded-xl">
              <p className="font-bold">✅ Booking Confirmed!</p>
              <p>📍 {booking.pickup} → {booking.destination}</p>
              <p>👥 {booking.passengers} passengers | 💷 {booking.fare} | ⏱️ {booking.eta}</p>
            </div>
          )}
          <div ref={messagesEndRef} />
        </div>

        {/* Text Input */}
        <div className="flex gap-2">
          <Input
            value={textInput}
            onChange={(e) => setTextInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && sendTextMessage()}
            placeholder="Or type a message..."
            className="flex-1"
          />
          <Button onClick={sendTextMessage} disabled={status !== "connected"}>
            Send
          </Button>
        </div>

        {/* Metrics */}
        <div className="bg-card rounded-xl border border-border p-4">
          <h3 className="font-semibold text-primary mb-3">📊 Latency Metrics</h3>
          <div className="grid grid-cols-2 gap-4 text-sm">
            <div className="flex justify-between">
              <span className="text-muted-foreground">Last response:</span>
              <span className="text-green-400 font-mono">{lastLatency ?? "--"}ms</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Average:</span>
              <span className="text-green-400 font-mono">{avgLatency ?? "--"}ms</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">First audio byte:</span>
              <span className="text-green-400 font-mono">{firstAudioLatency ?? "--"}ms</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Responses:</span>
              <span className="text-green-400 font-mono">{responseCount}</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
