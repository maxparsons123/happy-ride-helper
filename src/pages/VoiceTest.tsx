import { useState, useRef, useEffect, useCallback } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Mic, Phone, PhoneOff, Bot } from "lucide-react";
import { supabase } from "@/integrations/supabase/client";
import { TAXI_REALTIME_WS_URL } from "@/config/supabase";

const WS_URL = TAXI_REALTIME_WS_URL;

interface Agent {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  voice: string;
  is_active: boolean;
}

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
  
  // Agent selection
  const [agents, setAgents] = useState<Agent[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<string>("ada");
  const [selectedVoice, setSelectedVoice] = useState<string>("shimmer");
  const [loadingAgents, setLoadingAgents] = useState(true);
  
  // Raw passthrough mode
  const [rawPassthroughMode, setRawPassthroughMode] = useState(false);
  const [rawPassthroughEndpoint, setRawPassthroughEndpoint] = useState("");
  
  // Metrics
  const [lastLatency, setLastLatency] = useState<number | null>(null);
  const [avgLatency, setAvgLatency] = useState<number | null>(null);
  const [firstAudioLatency, setFirstAudioLatency] = useState<number | null>(null);
  const [responseCount, setResponseCount] = useState(0);

  // Audio Quality Test state
  const [isTestRecording, setIsTestRecording] = useState(false);
  const [testTranscript, setTestTranscript] = useState<string | null>(null);
  const [testLatency, setTestLatency] = useState<number | null>(null);
  const [testAudioUrl, setTestAudioUrl] = useState<string | null>(null);
  const [testAudioLevels, setTestAudioLevels] = useState<number[]>([]);
  const [testError, setTestError] = useState<string | null>(null);
  const testMediaRecorderRef = useRef<MediaRecorder | null>(null);
  const testAudioChunksRef = useRef<Blob[]>([]);
  const testAnalyserRef = useRef<AnalyserNode | null>(null);
  const testAnimationRef = useRef<number | null>(null);
  
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
  const audioSentRef = useRef(false); // Track if audio was actually sent

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages, booking]);

  // Fetch available agents
  useEffect(() => {
    const fetchAgents = async () => {
      try {
        const { data, error } = await supabase
          .from("agents")
          .select("id, name, slug, description, voice, is_active")
          .eq("is_active", true)
          .order("created_at", { ascending: true });

        if (error) throw error;
        setAgents(data || []);
        if (data && data.length > 0) {
          setSelectedAgent(data[0].slug);
        }
      } catch (error) {
        console.error("Error fetching agents:", error);
      } finally {
        setLoadingAgents(false);
      }
    };
    fetchAgents();
  }, []);

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
        const agentName = agents.find(a => a.slug === selectedAgent)?.name || selectedAgent;
        addMessage(`Connected, initializing session with ${agentName}...`, "system");
        
        const initPayload: Record<string, unknown> = {
          type: "init",
          call_id: "voice-test-" + Date.now(),
          addressTtsSplicing: true,
          agent: selectedAgent,
          voice: selectedVoice,
          useUnifiedExtraction: false,
        };
        
        // Add raw passthrough mode settings
        if (rawPassthroughMode && rawPassthroughEndpoint.trim()) {
          initPayload.bookingMode = "raw";
          initPayload.rawPassthroughEndpoint = rawPassthroughEndpoint.trim();
        }
        
        ws.send(JSON.stringify(initPayload));
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

      if (data.type === "address_tts") {
        // Play the high-fidelity address audio (same PCM16 24kHz format)
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
  }, [addMessage, playAudioChunk, updateMetrics, cleanupAudio, selectedAgent, agents, rawPassthroughMode, rawPassthroughEndpoint]);

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
    audioSentRef.current = false; // Reset audio sent flag

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
        
        // Mark that we actually sent audio
        audioSentRef.current = true;
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

    // Only send commit if we actually sent audio (prevents empty buffer error)
    if (wsRef.current?.readyState === WebSocket.OPEN && audioSentRef.current) {
      wsRef.current.send(JSON.stringify({ type: "commit" }));
    } else {
      console.log("Skipping commit - no audio was sent");
    }
    
    // Reset audio sent flag
    audioSentRef.current = false;

    setTimeout(() => {
      if (wsRef.current?.readyState === WebSocket.OPEN) {
        setVoiceStatus("Ready - hold to speak (or Space)");
      }
    }, 500);
  }, [stopRecordingInternal]);

  // === µ-LAW CODEC (Phone line simulation) ===
  const ULAW_BIAS = 0x84;
  const ULAW_CLIP = 32635;

  const linearToUlaw = (sample: number): number => {
    let sign = (sample < 0) ? 0x80 : 0;
    if (sign) sample = -sample;
    if (sample > ULAW_CLIP) sample = ULAW_CLIP;
    sample += ULAW_BIAS;
    let exponent = Math.floor(Math.log2(sample)) - 7;
    if (exponent < 0) exponent = 0;
    if (exponent > 7) exponent = 7;
    let mantissa = (sample >> (exponent + 3)) & 0x0F;
    return ~(sign | (exponent << 4) | mantissa) & 0xFF;
  };

  const ulawToLinear = (ulaw: number): number => {
    let u = ~ulaw & 0xFF;
    let sign = (u & 0x80) ? -1 : 1;
    let exponent = (u >> 4) & 0x07;
    let mantissa = u & 0x0F;
    let sample = ((mantissa << 3) + ULAW_BIAS) << exponent;
    return sign * (sample - ULAW_BIAS);
  };

  // Convert AudioBuffer to WAV blob
  const audioBufferToWav = (buffer: AudioBuffer): Blob => {
    const numChannels = 1;
    const sampleRate = buffer.sampleRate;
    const format = 1; // PCM
    const bitDepth = 16;
    const samples = buffer.getChannelData(0);
    const byteRate = sampleRate * numChannels * bitDepth / 8;
    const blockAlign = numChannels * bitDepth / 8;
    const dataSize = samples.length * numChannels * bitDepth / 8;
    const bufferSize = 44 + dataSize;
    
    const arrayBuffer = new ArrayBuffer(bufferSize);
    const view = new DataView(arrayBuffer);
    
    const writeString = (offset: number, str: string) => {
      for (let i = 0; i < str.length; i++) {
        view.setUint8(offset + i, str.charCodeAt(i));
      }
    };
    
    writeString(0, 'RIFF');
    view.setUint32(4, bufferSize - 8, true);
    writeString(8, 'WAVE');
    writeString(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, format, true);
    view.setUint16(22, numChannels, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, byteRate, true);
    view.setUint16(32, blockAlign, true);
    view.setUint16(34, bitDepth, true);
    writeString(36, 'data');
    view.setUint32(40, dataSize, true);
    
    let offset = 44;
    for (let i = 0; i < samples.length; i++) {
      const s = Math.max(-1, Math.min(1, samples[i]));
      view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
      offset += 2;
    }
    
    return new Blob([arrayBuffer], { type: 'audio/wav' });
  };

  // Simulate phone line: resample to 8kHz, apply µ-law, then decode back
  const simulatePhoneLine = async (audioBuffer: AudioBuffer): Promise<AudioBuffer> => {
    const inputRate = audioBuffer.sampleRate;
    const outputRate = 8000;
    const inputData = audioBuffer.getChannelData(0);
    
    // Resample to 8kHz
    const ratio = outputRate / inputRate;
    const outputLength = Math.floor(inputData.length * ratio);
    const resampled = new Float32Array(outputLength);
    
    for (let i = 0; i < outputLength; i++) {
      const srcIndex = i / ratio;
      const srcIndexFloor = Math.floor(srcIndex);
      const srcIndexCeil = Math.min(srcIndexFloor + 1, inputData.length - 1);
      const t = srcIndex - srcIndexFloor;
      resampled[i] = inputData[srcIndexFloor] * (1 - t) + inputData[srcIndexCeil] * t;
    }

    // Apply µ-law encode/decode (lossy!) + noise gate
    const processed = new Float32Array(outputLength);
    for (let i = 0; i < outputLength; i++) {
      const sample16 = Math.max(-32768, Math.min(32767, Math.round(resampled[i] * 32768)));
      const ulaw = linearToUlaw(sample16);
      const decoded = ulawToLinear(ulaw);
      // Soft-knee noise gate (threshold 25, floor 0.15) - matches bridge DSP
      const abs = Math.abs(decoded);
      const kneeHigh = 75;
      const gain = abs < 25 ? 0.15 : abs > kneeHigh ? 1.0 : 0.15 + 0.85 * ((abs - 25) / 50);
      processed[i] = (decoded * gain) / 32768;
    }

    const offlineCtx = new OfflineAudioContext(1, outputLength, outputRate);
    const buffer = offlineCtx.createBuffer(1, outputLength, outputRate);
    buffer.getChannelData(0).set(processed);
    return buffer;
  };

  // Phone simulation state
  const [phoneSimEnabled, setPhoneSimEnabled] = useState(true);
  const [phoneSimAudioUrl, setPhoneSimAudioUrl] = useState<string | null>(null);

  // === AUDIO QUALITY TEST FUNCTIONS ===
  const startAudioTest = useCallback(async () => {
    setTestTranscript(null);
    setTestLatency(null);
    setTestAudioUrl(null);
    setTestAudioLevels([]);
    setTestError(null);
    testAudioChunksRef.current = [];

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          sampleRate: 16000,
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true
        }
      });

      // Set up audio analyser for visualizing levels
      const audioCtx = new AudioContext({ sampleRate: 16000 });
      const source = audioCtx.createMediaStreamSource(stream);
      const analyser = audioCtx.createAnalyser();
      analyser.fftSize = 256;
      source.connect(analyser);
      testAnalyserRef.current = analyser;

      // Visualize audio levels
      const dataArray = new Uint8Array(analyser.frequencyBinCount);
      const updateLevels = () => {
        if (!testAnalyserRef.current) return;
        testAnalyserRef.current.getByteFrequencyData(dataArray);
        // Get average level across first 32 bins (lower frequencies - speech)
        const avg = dataArray.slice(0, 32).reduce((a, b) => a + b, 0) / 32;
        setTestAudioLevels(prev => [...prev.slice(-50), avg]);
        testAnimationRef.current = requestAnimationFrame(updateLevels);
      };
      updateLevels();

      // Start MediaRecorder
      const mediaRecorder = new MediaRecorder(stream, { mimeType: 'audio/webm' });
      testMediaRecorderRef.current = mediaRecorder;

      mediaRecorder.ondataavailable = (e) => {
        if (e.data.size > 0) {
          testAudioChunksRef.current.push(e.data);
        }
      };

      mediaRecorder.onstop = async () => {
        // Stop visualization
        if (testAnimationRef.current) {
          cancelAnimationFrame(testAnimationRef.current);
          testAnimationRef.current = null;
        }
        testAnalyserRef.current = null;
        stream.getTracks().forEach(t => t.stop());

        // Create original playback URL
        const blob = new Blob(testAudioChunksRef.current, { type: 'audio/webm' });
        const url = URL.createObjectURL(blob);
        setTestAudioUrl(url);

        // Apply phone line simulation if enabled
        if (phoneSimEnabled) {
          try {
            const arrayBuffer = await blob.arrayBuffer();
            const tempCtx = new AudioContext();
            const originalBuffer = await tempCtx.decodeAudioData(arrayBuffer);
            const phoneBuffer = await simulatePhoneLine(originalBuffer);
            const wavBlob = audioBufferToWav(phoneBuffer);
            const phoneUrl = URL.createObjectURL(wavBlob);
            setPhoneSimAudioUrl(phoneUrl);
            tempCtx.close();
          } catch (err) {
            console.error('Phone simulation error:', err);
          }
        } else {
          setPhoneSimAudioUrl(null);
        }

        audioCtx.close();

        // Send to STT
        const startTime = Date.now();
        try {
          const reader = new FileReader();
          reader.readAsDataURL(blob);
          reader.onloadend = async () => {
            const base64Audio = (reader.result as string).split(',')[1];
            
            const { data, error } = await supabase.functions.invoke('taxi-stt', {
              body: {
                audio: base64Audio,
                call_id: 'audio-test-' + Date.now()
              }
            });

            const latency = Date.now() - startTime;
            setTestLatency(latency);

            if (error) {
              setTestError(`STT Error: ${error.message}`);
            } else if (data?.text) {
              setTestTranscript(data.text);
            } else {
              setTestError('No transcript returned');
            }
          };
        } catch (err) {
          setTestError(`Error: ${(err as Error).message}`);
        }
      };

      mediaRecorder.start();
      setIsTestRecording(true);
    } catch (err) {
      setTestError(`Microphone error: ${(err as Error).message}`);
    }
  }, []);

  const stopAudioTest = useCallback(() => {
    if (testMediaRecorderRef.current && testMediaRecorderRef.current.state === 'recording') {
      testMediaRecorderRef.current.stop();
    }
    setIsTestRecording(false);
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
        <div className="flex flex-wrap gap-3 items-center">
          {/* Agent Selector */}
          <div className="flex items-center gap-2">
            <Bot className="w-4 h-4 text-muted-foreground" />
            <Select
              value={selectedAgent}
              onValueChange={setSelectedAgent}
              disabled={status !== "disconnected" || loadingAgents}
            >
              <SelectTrigger className="w-[180px] bg-card border-chat-border">
                <SelectValue placeholder="Select agent..." />
              </SelectTrigger>
              <SelectContent className="bg-card border-chat-border">
                {agents.map((agent) => (
                  <SelectItem key={agent.id} value={agent.slug}>
                    <div className="flex flex-col">
                      <span className="font-medium">{agent.name}</span>
                      <span className="text-xs text-muted-foreground">{agent.voice}</span>
                    </div>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {/* Voice Selector */}
          <div className="flex items-center gap-2">
            <Mic className="w-4 h-4 text-muted-foreground" />
            <Select
              value={selectedVoice}
              onValueChange={setSelectedVoice}
              disabled={status !== "disconnected"}
            >
              <SelectTrigger className="w-[120px] bg-card border-chat-border">
                <SelectValue placeholder="Voice" />
              </SelectTrigger>
              <SelectContent className="bg-card border-chat-border">
                <SelectItem value="shimmer">Shimmer</SelectItem>
                <SelectItem value="alloy">Alloy</SelectItem>
                <SelectItem value="echo">Echo</SelectItem>
                <SelectItem value="fable">Fable</SelectItem>
                <SelectItem value="onyx">Onyx</SelectItem>
                <SelectItem value="nova">Nova</SelectItem>
                <SelectItem value="ash">Ash</SelectItem>
                <SelectItem value="coral">Coral</SelectItem>
                <SelectItem value="sage">Sage</SelectItem>
                <SelectItem value="verse">Verse</SelectItem>
              </SelectContent>
            </Select>
          </div>

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

        {/* Raw Passthrough Mode */}
        <div className="bg-card rounded-xl border border-chat-border p-4">
          <div className="flex items-center justify-between mb-3">
            <div>
              <h3 className="font-semibold text-primary">🔀 Raw Passthrough Mode</h3>
              <p className="text-xs text-muted-foreground">
                Send booking data directly to your webhook for validation
              </p>
            </div>
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={rawPassthroughMode}
                onChange={(e) => setRawPassthroughMode(e.target.checked)}
                disabled={status !== "disconnected"}
                className="rounded border-muted-foreground w-5 h-5"
              />
              <span className="text-sm font-medium">{rawPassthroughMode ? "ON" : "OFF"}</span>
            </label>
          </div>
          
          {rawPassthroughMode && (
            <div className="space-y-2">
              <label className="text-sm text-muted-foreground">Webhook Endpoint:</label>
              <Input
                value={rawPassthroughEndpoint}
                onChange={(e) => setRawPassthroughEndpoint(e.target.value)}
                placeholder="https://your-server.com/ada-webhook"
                disabled={status !== "disconnected"}
                className="font-mono text-sm"
              />
              <p className="text-xs text-muted-foreground">
                Ada will POST raw booking details to this endpoint. Your server should respond with JSON containing <code className="bg-muted px-1 rounded">action</code>, <code className="bg-muted px-1 rounded">message</code>, etc.
              </p>
            </div>
          )}
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

        {/* Audio Quality Test */}
        <div className="bg-card rounded-xl border border-chat-border p-4">
          <h3 className="font-semibold text-primary mb-3">🎤 Audio Quality Test</h3>
          <p className="text-xs text-muted-foreground mb-4">
            Record your voice to hear what it sounds like on a phone line. Say "cancel" to test.
          </p>
          
          <div className="flex items-center gap-4 mb-4">
            <Button
              onClick={isTestRecording ? stopAudioTest : startAudioTest}
              variant={isTestRecording ? "destructive" : "default"}
              className={isTestRecording ? "animate-pulse" : ""}
            >
              <Mic className="w-4 h-4 mr-2" />
              {isTestRecording ? "Stop Recording" : "Test Audio"}
            </Button>

            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={phoneSimEnabled}
                onChange={(e) => setPhoneSimEnabled(e.target.checked)}
                className="rounded border-muted-foreground"
              />
              <span className="text-muted-foreground">📞 Phone simulation</span>
            </label>
          </div>

          {/* Audio Playback */}
          {(testAudioUrl || phoneSimAudioUrl) && (
            <div className="space-y-2 mb-4">
              {testAudioUrl && (
                <div className="flex items-center gap-2">
                  <span className="text-xs text-muted-foreground w-20">Original:</span>
                  <audio controls src={testAudioUrl} className="h-8 flex-1" />
                </div>
              )}
              {phoneSimAudioUrl && (
                <div className="flex items-center gap-2">
                  <span className="text-xs text-orange-400 w-20">📞 Phone:</span>
                  <audio controls src={phoneSimAudioUrl} className="h-8 flex-1" />
                </div>
              )}
            </div>
          )}

          {/* Audio Level Visualization */}
          {testAudioLevels.length > 0 && (
            <div className="flex items-end gap-0.5 h-12 mb-4 bg-muted/30 rounded p-2">
              {testAudioLevels.map((level, i) => (
                <div
                  key={i}
                  className="flex-1 bg-primary rounded-t transition-all"
                  style={{ height: `${Math.min(100, level / 2.55)}%` }}
                />
              ))}
            </div>
          )}

          {/* Results */}
          {testTranscript !== null && (
            <div className="p-3 bg-muted/50 rounded-lg mb-2">
              <p className="text-xs text-muted-foreground mb-1">STT Result:</p>
              <p className="font-mono text-lg">"{testTranscript}"</p>
              {testLatency && (
                <p className="text-xs text-green-400 mt-1">⚡ {testLatency}ms</p>
              )}
            </div>
          )}

          {testError && (
            <div className="p-3 bg-red-500/20 border border-red-500/50 rounded-lg text-red-400 text-sm">
              {testError}
            </div>
          )}
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
