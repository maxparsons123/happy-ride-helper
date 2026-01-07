import { useState, useRef, useEffect, useCallback } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Mic, Phone, PhoneOff } from "lucide-react";

const WS_URL = "wss://xsdlzoyaosfbbwzmcinq.supabase.co/functions/v1/taxi-realtime";

interface Message {
  text: string;
  type: "user" | "assistant" | "system";
  latency?: number;
}

interface Booking {
  pickup: string;
  destination: string;
  passengers: number;
  fare: string;
  eta: string;
}

export default function VoiceTest() {
  const [status, setStatus] = useState<"disconnected" | "connecting" | "connected">("disconnected");
  const [messages, setMessages] = useState<Message[]>([]);
  const [booking, setBooking] = useState<Booking | null>(null);
  const [isRecording, setIsRecording] = useState(false);
  const [isSpeaking, setIsSpeaking] = useState(false);
  const [voiceStatus, setVoiceStatus] = useState("Connect first...");
  const [textInput, setTextInput] = useState("");
  
  // Metrics
  const [lastLatency, setLastLatency] = useState<number | null>(null);
  const [avgLatency, setAvgLatency] = useState<number | null>(null);
  const [firstAudioLatency, setFirstAudioLatency] = useState<number | null>(null);
  const [responseCount, setResponseCount] = useState(0);
  
  // Refs for WebSocket and audio
  const wsRef = useRef<WebSocket | null>(null);
  const audioContextRef = useRef<AudioContext | null>(null);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const workletNodeRef = useRef<AudioWorkletNode | null>(null);
  const currentTranscriptRef = useRef("");
  const speechStartTimeRef = useRef(0);
  const firstAudioTimeRef = useRef(0);
  const latenciesRef = useRef<number[]>([]);
  const nextStartTimeRef = useRef(0);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const isConnectingRef = useRef(false); // Guard double-connect

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages, booking]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      cleanupAudio();
      wsRef.current?.close();
    };
  }, []);

  // Keyboard controls: Space to talk
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
    // Cleanup worklet
    if (workletNodeRef.current) {
      workletNodeRef.current.disconnect();
      workletNodeRef.current = null;
    }
    // Cleanup media stream
    if (mediaStreamRef.current) {
      mediaStreamRef.current.getTracks().forEach(t => t.stop());
      mediaStreamRef.current = null;
    }
    // Cleanup audio context
    if (audioContextRef.current && audioContextRef.current.state !== "closed") {
      audioContextRef.current.close().catch(() => {});
    }
    audioContextRef.current = null;
    nextStartTimeRef.current = 0;
  }, []);

  const addMessage = useCallback((text: string, type: Message["type"], latency?: number) => {
    setMessages(prev => [...prev, { text, type, latency }]);
  }, []);

  const updateMetrics = useCallback((latency: number) => {
    latenciesRef.current.push(latency);
    setLastLatency(latency);
    setAvgLatency(Math.round(latenciesRef.current.reduce((a, b) => a + b, 0) / latenciesRef.current.length));
    setResponseCount(latenciesRef.current.length);
  }, []);

  const playAudioChunk = useCallback((base64Audio: string) => {
    console.log("Playing audio chunk, length:", base64Audio.length);
    
    // Create or reuse audio context
    if (!audioContextRef.current || audioContextRef.current.state === "closed") {
      audioContextRef.current = new AudioContext({ sampleRate: 24000 });
      nextStartTimeRef.current = 0;
    }
    
    const ctx = audioContextRef.current;
    
    // Resume if suspended (browser autoplay policy)
    if (ctx.state === "suspended") {
      ctx.resume();
    }

    try {
      // Decode Base64 to ArrayBuffer
      const binary = atob(base64Audio);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
      }

      // Convert PCM16 to Float32
      const int16 = new Int16Array(bytes.buffer);
      const float32 = new Float32Array(int16.length);
      for (let i = 0; i < int16.length; i++) {
        float32[i] = int16[i] / 32768.0;
      }

      const audioBuffer = ctx.createBuffer(1, float32.length, 24000);
      audioBuffer.getChannelData(0).set(float32);

      const source = ctx.createBufferSource();
      source.buffer = audioBuffer;
      source.connect(ctx.destination);

      // JITTER BUFFER: Schedule chunks precisely to avoid clicks
      const now = ctx.currentTime;
      if (nextStartTimeRef.current < now) {
        nextStartTimeRef.current = now + 0.05; // 50ms safety buffer
      }
      
      source.start(nextStartTimeRef.current);
      nextStartTimeRef.current += audioBuffer.duration;
    } catch (error) {
      console.error("Error playing audio:", error);
    }
  }, []);

  const connect = useCallback(async () => {
    // Guard against double-connect
    if (isConnectingRef.current || wsRef.current?.readyState === WebSocket.OPEN) {
      console.log("Already connected or connecting");
      return;
    }

    isConnectingRef.current = true;
    setStatus("connecting");
    addMessage("Connecting to taxi AI...", "system");

    // Initialize audio context
    cleanupAudio();
    audioContextRef.current = new AudioContext({ sampleRate: 24000 });
    if (audioContextRef.current.state === "suspended") {
      await audioContextRef.current.resume();
    }

    const ws = new WebSocket(WS_URL);
    wsRef.current = ws;

    ws.onopen = () => {
      console.log("WebSocket opened");
      addMessage("Connected, initializing session...", "system");
      
      ws.send(JSON.stringify({
        type: "init",
        call_id: "voice-test-" + Date.now()
      }));
    };

    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);
      console.log("Received:", data);

      if (data.type === "session_ready") {
        setStatus("connected");
        isConnectingRef.current = false;
        addMessage("Session ready! Hold mic button or press Space to speak.", "system");
        setVoiceStatus("Ready - hold to speak (or Space)");
      }

      if (data.type === "ai_speaking") {
        setIsSpeaking(Boolean(data.speaking));
      }

      if (data.type === "audio" && speechStartTimeRef.current > 0 && firstAudioTimeRef.current === 0) {
        firstAudioTimeRef.current = Date.now();
        const latency = firstAudioTimeRef.current - speechStartTimeRef.current;
        setFirstAudioLatency(latency);
        updateMetrics(latency);
        console.log("First audio received, latency:", latency, "ms");
      }

      if (data.type === "audio") {
        setIsSpeaking(true);
        playAudioChunk(data.audio);
      }

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

      if (data.type === "booking_confirmed") {
        setBooking(data.booking);
      }

      if (data.type === "error") {
        addMessage("Error: " + JSON.stringify(data.error), "system");
      }
    };

    ws.onclose = () => {
      console.log("WebSocket closed");
      setStatus("disconnected");
      setIsSpeaking(false);
      setIsRecording(false);
      isConnectingRef.current = false;
      addMessage("Disconnected", "system");
      setVoiceStatus("Connect first...");
      cleanupAudio();
    };

    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      isConnectingRef.current = false;
      addMessage("Connection error", "system");
    };
  }, [addMessage, playAudioChunk, updateMetrics, cleanupAudio]);

  const disconnect = useCallback(() => {
    stopRecordingInternal();
    setIsSpeaking(false);
    cleanupAudio();
    wsRef.current?.close();
    wsRef.current = null;
    isConnectingRef.current = false;
  }, [cleanupAudio]);

  // Internal stop without state dependency issues
  const stopRecordingInternal = useCallback(() => {
    if (workletNodeRef.current) {
      workletNodeRef.current.disconnect();
      workletNodeRef.current = null;
    }
    if (mediaStreamRef.current) {
      mediaStreamRef.current.getTracks().forEach(t => t.stop());
      mediaStreamRef.current = null;
    }
  }, []);

  const startRecording = useCallback(async () => {
    if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) {
      console.log("Cannot record: WebSocket not open");
      return;
    }
    if (mediaStreamRef.current) {
      console.log("Already recording");
      return;
    }

    console.log("Starting recording with AudioWorklet...");
    setIsRecording(true);
    setVoiceStatus("🔴 Recording...");
    speechStartTimeRef.current = Date.now();
    firstAudioTimeRef.current = 0;

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          sampleRate: 24000,
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true
        }
      });
      mediaStreamRef.current = stream;
      console.log("Got mic stream");

      // Create or reuse audio context
      let ctx = audioContextRef.current;
      if (!ctx || ctx.state === "closed") {
        ctx = new AudioContext({ sampleRate: 24000 });
        audioContextRef.current = ctx;
        nextStartTimeRef.current = 0;
      }
      if (ctx.state === "suspended") {
        await ctx.resume();
      }

      // Load AudioWorklet module
      try {
        await ctx.audioWorklet.addModule("/audio-worklet.js");
      } catch (e) {
        // Module might already be loaded
        console.log("Worklet module load:", e);
      }

      const source = ctx.createMediaStreamSource(stream);
      const workletNode = new AudioWorkletNode(ctx, "recorder");
      workletNodeRef.current = workletNode;

      // Handle audio data from worklet
      workletNode.port.onmessage = (e) => {
        if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;

        const buffer = e.data as ArrayBuffer;
        const uint8 = new Uint8Array(buffer);
        
        // Convert to base64
        let binary = "";
        for (let i = 0; i < uint8.length; i++) {
          binary += String.fromCharCode(uint8[i]);
        }
        const base64 = btoa(binary);

        wsRef.current.send(JSON.stringify({
          type: "audio",
          audio: base64
        }));
      };

      source.connect(workletNode);
      // Don't connect worklet to destination (we don't want to hear ourselves)
      console.log("AudioWorklet recording started");
    } catch (error) {
      console.error("Mic error:", error);
      addMessage("Microphone access denied: " + (error as Error).message, "system");
      setIsRecording(false);
      setVoiceStatus("Ready - hold to speak (or Space)");
    }
  }, [addMessage]);

  const stopRecording = useCallback(() => {
    if (!mediaStreamRef.current) return;

    console.log("Stopping recording...");
    setIsRecording(false);
    setVoiceStatus("Processing...");

    stopRecordingInternal();

    // Tell the backend we're done speaking (push-to-talk)
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify({ type: "commit" }));
    }

    setTimeout(() => {
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        setVoiceStatus("Ready - hold to speak (or Space)");
      }
    }, 500);
  }, [stopRecordingInternal]);

  const sendTextMessage = useCallback(() => {
    if (!textInput.trim() || !wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;

    speechStartTimeRef.current = Date.now();
    firstAudioTimeRef.current = 0;

    wsRef.current.send(JSON.stringify({
      type: "text",
      text: textInput
    }));

    addMessage(textInput, "user");
    setTextInput("");
  }, [textInput, addMessage]);

  // Check for reduced motion preference
  const prefersReducedMotion = typeof window !== "undefined" 
    && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  return (
    <div className="min-h-screen bg-gradient-dark p-6">
      <div className="max-w-2xl mx-auto space-y-4">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-display font-bold text-primary">🚕 Voice Test</h1>
          <div className={`px-4 py-2 rounded-full text-sm font-medium ${
            status === "connected" ? "bg-green-600" : 
            status === "connecting" ? "bg-orange-500" : "bg-red-600"
          }`}>
            {status.charAt(0).toUpperCase() + status.slice(1)}
          </div>
        </div>

        {/* Controls */}
        <div className="flex gap-3">
          <Button 
            onClick={connect} 
            disabled={status === "connected" || status === "connecting"} 
            className="bg-gradient-gold"
          >
            <Phone className="w-4 h-4 mr-2" />
            Connect
          </Button>
          <Button onClick={disconnect} variant="outline" disabled={status === "disconnected"}>
            <PhoneOff className="w-4 h-4 mr-2" />
            Disconnect
          </Button>
        </div>

        {/* Voice Area */}
        <div className="flex items-center gap-4 p-4 bg-card rounded-xl border border-chat-border">
          {/* Mic Button */}
          <button
            onMouseDown={startRecording}
            onMouseUp={stopRecording}
            onMouseLeave={() => isRecording && stopRecording()}
            onTouchStart={(e) => { e.preventDefault(); startRecording(); }}
            onTouchEnd={(e) => { e.preventDefault(); stopRecording(); }}
            disabled={status !== "connected"}
            style={{ touchAction: "none" }}
            aria-label={isRecording ? "Release to stop recording" : "Hold to speak"}
            aria-pressed={isRecording}
            className={`w-16 h-16 rounded-full flex items-center justify-center transition-all select-none focus:outline-none focus:ring-2 focus:ring-primary focus:ring-offset-2 focus:ring-offset-background ${
              isRecording 
                ? `bg-green-500 ${prefersReducedMotion ? "" : "animate-pulse"}` 
                : status === "connected"
                  ? "bg-red-500 hover:bg-red-600"
                  : "bg-muted cursor-not-allowed"
            }`}
          >
            <Mic className="w-6 h-6 text-white" />
          </button>
          
          <div className="flex-1">
            <p className="font-semibold">Hold to speak</p>
            <p className="text-sm text-muted-foreground">{voiceStatus}</p>
            <p className="text-xs text-muted-foreground mt-1">Or press Space on keyboard</p>
          </div>

          {/* AI Speaking Indicator */}
          {isSpeaking && (
            <div className="flex items-center gap-2 px-3 py-2 bg-primary/20 rounded-full">
              <div className={`flex items-center gap-1 ${prefersReducedMotion ? "" : ""}`}>
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
        <div className="h-80 overflow-y-auto bg-card rounded-xl border border-chat-border p-4 space-y-3">
          {messages.map((msg, i) => (
            <div
              key={i}
              className={`max-w-[85%] p-3 rounded-xl ${
                msg.type === "user" 
                  ? "ml-auto bg-primary text-primary-foreground" 
                  : msg.type === "assistant"
                    ? "bg-muted"
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
            <div className="bg-green-600 text-white p-4 rounded-xl">
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
        <div className="bg-card rounded-xl border border-chat-border p-4">
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
