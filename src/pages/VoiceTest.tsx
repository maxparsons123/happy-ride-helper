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
  const audioQueueRef = useRef<Float32Array[]>([]);
  const isPlayingRef = useRef(false);
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

  const playQueue = useCallback(async () => {
    if (audioQueueRef.current.length === 0) {
      isPlayingRef.current = false;
      return;
    }
    isPlayingRef.current = true;

    const samples = audioQueueRef.current.shift()!;
    const ctx = audioContextRef.current!;
    const buffer = ctx.createBuffer(1, samples.length, 24000);
    buffer.copyToChannel(new Float32Array(samples.buffer.slice(0) as ArrayBuffer), 0);

    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.connect(ctx.destination);
    source.onended = () => playQueue();
    source.start();
  }, []);

  const playAudioChunk = useCallback((base64Audio: string) => {
    if (!audioContextRef.current) {
      audioContextRef.current = new AudioContext({ sampleRate: 24000 });
    }

    const binaryString = atob(base64Audio);
    const bytes = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
      bytes[i] = binaryString.charCodeAt(i);
    }

    const int16 = new Int16Array(bytes.buffer);
    const float32 = new Float32Array(int16.length);
    for (let i = 0; i < int16.length; i++) {
      float32[i] = int16[i] / 32768;
    }

    audioQueueRef.current.push(new Float32Array(float32));
    if (!isPlayingRef.current) playQueue();
  }, [playQueue]);

  const connect = useCallback(async () => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      addMessage("Already connected", "system");
      return;
    }

    setStatus("connecting");
    addMessage("Connecting to taxi AI...", "system");

    audioContextRef.current = new AudioContext({ sampleRate: 24000 });

    const ws = new WebSocket(WS_URL);
    wsRef.current = ws;

    ws.onopen = () => {
      setStatus("connected");
      addMessage("Connected! Hold mic button to speak.", "system");
      setVoiceStatus("Ready - hold to speak");
      
      ws.send(JSON.stringify({
        type: "init",
        call_id: "voice-test-" + Date.now()
      }));
    };

    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);
      console.log("Received:", data);

      if (data.type === "audio" && speechStartTimeRef.current > 0 && firstAudioTimeRef.current === 0) {
        firstAudioTimeRef.current = Date.now();
        const latency = firstAudioTimeRef.current - speechStartTimeRef.current;
        setFirstAudioLatency(latency);
        updateMetrics(latency);
      }

      if (data.type === "audio") {
        playAudioChunk(data.audio);
      }

      if (data.type === "transcript") {
        if (data.role === "assistant") {
          currentTranscriptRef.current += data.text;
        } else if (data.role === "user") {
          addMessage(data.text, "user");
        }
      }

      if (data.type === "response_done" && currentTranscriptRef.current) {
        const responseLatency = speechStartTimeRef.current > 0 ? Date.now() - speechStartTimeRef.current : undefined;
        addMessage(currentTranscriptRef.current, "assistant", responseLatency);
        currentTranscriptRef.current = "";
        speechStartTimeRef.current = 0;
        firstAudioTimeRef.current = 0;
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
    wsRef.current?.close();
    wsRef.current = null;
  }, []);

  const startRecording = useCallback(async () => {
    if (!wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;
    if (isRecording) return;

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

      const ctx = new AudioContext({ sampleRate: 24000 });
      audioContextRef.current = ctx;
      const source = ctx.createMediaStreamSource(stream);
      const processor = ctx.createScriptProcessor(4096, 1, 1);
      processorRef.current = processor;

      processor.onaudioprocess = (e) => {
        if (!isRecording || !wsRef.current || wsRef.current.readyState !== WebSocket.OPEN) return;

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

      setIsRecording(true);
      speechStartTimeRef.current = Date.now();
      firstAudioTimeRef.current = 0;
      setVoiceStatus("🔴 Recording...");
    } catch (error) {
      console.error("Mic error:", error);
      addMessage("Microphone access denied", "system");
    }
  }, [isRecording, addMessage]);

  const stopRecording = useCallback(() => {
    if (!isRecording) return;

    setIsRecording(false);
    setVoiceStatus("Processing...");

    processorRef.current?.disconnect();
    processorRef.current = null;
    mediaStreamRef.current?.getTracks().forEach(t => t.stop());
    mediaStreamRef.current = null;

    setTimeout(() => {
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        setVoiceStatus("Ready - hold to speak");
      }
    }, 500);
  }, [isRecording]);

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
          <div>
            <p className="font-semibold">Hold to speak</p>
            <p className="text-sm text-muted-foreground">{voiceStatus}</p>
          </div>
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
