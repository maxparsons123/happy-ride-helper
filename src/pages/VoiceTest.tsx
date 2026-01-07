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
  
  const wsRef = useRef<WebSocket | null>(null);
  const audioContextRef = useRef<AudioContext | null>(null);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const processorRef = useRef<ScriptProcessorNode | null>(null);
  const currentTranscriptRef = useRef("");
  const speechStartTimeRef = useRef(0);
  const firstAudioTimeRef = useRef(0);
  const latenciesRef = useRef<number[]>([]);
  const nextStartTimeRef = useRef(0); // Global variable to track timing for smooth playback
  const messagesEndRef = useRef<HTMLDivElement>(null);

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages, booking]);

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
    
    if (!audioContextRef.current || audioContextRef.current.state === "closed") {
      audioContextRef.current = new AudioContext({ sampleRate: 24000 });
      nextStartTimeRef.current = 0; // reset scheduling when recreating context
    }
    
    // Resume AudioContext if suspended (browser autoplay policy)
    if (audioContextRef.current.state === "suspended") {
      audioContextRef.current.resume();
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

      const ctx = audioContextRef.current;
      const audioBuffer = ctx.createBuffer(1, float32.length, 24000);
      audioBuffer.getChannelData(0).set(float32);

      const source = ctx.createBufferSource();
      source.buffer = audioBuffer;
      source.connect(ctx.destination);

      // SMOOTHING LOGIC: Prevents "pops" between chunks by scheduling playback
      const now = ctx.currentTime;
      if (nextStartTimeRef.current < now) {
        nextStartTimeRef.current = now + 0.01; // Add a tiny 10ms buffer to prevent jitter
      }
      
      source.start(nextStartTimeRef.current);
      nextStartTimeRef.current += audioBuffer.duration;
    } catch (error) {
      console.error("Error playing audio:", error);
    }
  }, []);

  const connect = useCallback(async () => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      addMessage("Already connected", "system");
      return;
    }

    setStatus("connecting");
    addMessage("Connecting to taxi AI...", "system");

    if (!audioContextRef.current || audioContextRef.current.state === "closed") {
      audioContextRef.current = new AudioContext({ sampleRate: 24000 });
    }
    if (audioContextRef.current.state === "suspended") {
      void audioContextRef.current.resume();
    }
    nextStartTimeRef.current = 0;

    const ws = new WebSocket(WS_URL);
    wsRef.current = ws;

    ws.onopen = () => {
      setStatus("connecting");
      addMessage("Connected, initializing session...", "system");
      
      ws.send(JSON.stringify({
        type: "init",
        call_id: "voice-test-" + Date.now()
      }));
    };

    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);
      console.log("Received:", data);

      // Session ready
      if (data.type === "session_ready") {
        setStatus("connected");
        addMessage("Session ready! Hold mic button to speak.", "system");
        setVoiceStatus("Ready - hold to speak");
      }

      // AI speaking state (sent by backend on response.created/response.done)
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
        console.log("Received audio chunk");
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
      setStatus("disconnected");
      setIsSpeaking(false);
      nextStartTimeRef.current = 0;
      addMessage("Disconnected", "system");
      setVoiceStatus("Connect first...");
    };

    ws.onerror = (error) => {
      console.error("WebSocket error:", error);
      addMessage("Connection error", "system");
    };
  }, [addMessage, playAudioChunk, updateMetrics]);

  const disconnect = useCallback(() => {
    stopRecording();
    setIsSpeaking(false);
    nextStartTimeRef.current = 0;

    try {
      void audioContextRef.current?.close();
    } catch {
      // ignore
    }
    audioContextRef.current = null;

    wsRef.current?.close();
    wsRef.current = null;
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

    console.log("Starting recording...");
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
          noiseSuppression: true
        }
      });
      mediaStreamRef.current = stream;
      console.log("Got mic stream");

      let ctx = audioContextRef.current;
      const needsNewCtx = !ctx || ctx.state === "closed";
      if (needsNewCtx) {
        ctx = new AudioContext({ sampleRate: 24000 });
        audioContextRef.current = ctx;
        nextStartTimeRef.current = 0;
      }
      if (ctx.state === "suspended") {
        void ctx.resume();
      }

      const source = ctx.createMediaStreamSource(stream);
      const processor = ctx.createScriptProcessor(4096, 1, 1);
      processorRef.current = processor;

      processor.onaudioprocess = (e) => {
        if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;

        const inputData = e.inputBuffer.getChannelData(0);
        const int16 = new Int16Array(inputData.length);
        for (let i = 0; i < inputData.length; i++) {
          const s = Math.max(-1, Math.min(1, inputData[i]));
          int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
        }

        const uint8 = new Uint8Array(int16.buffer);
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

      source.connect(processor);
      processor.connect(ctx.destination);
      console.log("Audio processing started");
    } catch (error) {
      console.error("Mic error:", error);
      addMessage("Microphone access denied: " + (error as Error).message, "system");
      setIsRecording(false);
      setVoiceStatus("Ready - hold to speak");
    }
  }, [addMessage]);

  const stopRecording = useCallback(() => {
    if (!mediaStreamRef.current) return;

    console.log("Stopping recording...");
    setIsRecording(false);
    setVoiceStatus("Processing...");

    processorRef.current?.disconnect();
    processorRef.current = null;
    mediaStreamRef.current?.getTracks().forEach((t) => t.stop());
    mediaStreamRef.current = null;

    // Tell the backend we're done speaking (push-to-talk)
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify({ type: "commit" }));
    }

    setTimeout(() => {
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        setVoiceStatus("Ready - hold to speak");
      }
    }, 500);
  }, []);

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
          <Button onClick={connect} disabled={status === "connected"} className="bg-gradient-gold">
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
            onTouchStart={startRecording}
            onTouchEnd={stopRecording}
            disabled={status !== "connected"}
            className={`w-16 h-16 rounded-full flex items-center justify-center transition-all ${
              isRecording 
                ? "bg-green-500 animate-pulse" 
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
          </div>

          {/* AI Speaking Indicator */}
          {isSpeaking && (
            <div className="flex items-center gap-2 px-3 py-2 bg-primary/20 rounded-full">
              <div className="flex items-center gap-1">
                <span className="w-1 h-3 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_infinite]" />
                <span className="w-1 h-4 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.1s_infinite]" />
                <span className="w-1 h-2 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.2s_infinite]" />
                <span className="w-1 h-5 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.3s_infinite]" />
                <span className="w-1 h-3 bg-primary rounded-full animate-[pulse_0.6s_ease-in-out_0.4s_infinite]" />
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
