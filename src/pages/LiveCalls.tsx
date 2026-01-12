import { useEffect, useState, useRef, useCallback } from "react";
import { Link } from "react-router-dom";
import { supabase } from "@/integrations/supabase/client";
import { Card } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog";
import { Phone, PhoneOff, MapPin, Users, Clock, DollarSign, Radio, Volume2, VolumeX, ArrowLeft, CheckCircle2, XCircle, Loader2, User, History, Bot, AlertCircle, Trash2 } from "lucide-react";
import { toast } from "sonner";

interface Agent {
  id: string;
  name: string;
  slug: string;
}

interface Transcript {
  role: string;
  text: string;
  timestamp: string;
}

interface GeocodeResult {
  found: boolean;
  address: string;
  display_name?: string;
  place_name?: string; // Business/place name from Google (e.g., "Birmingham Airport")
  lat?: number;
  lon?: number;
  error?: string;
  loading?: boolean;
  needs_disambiguation?: boolean; // Multiple areas found, user must choose
  disambiguation_areas?: string[]; // List of area options
}

interface LiveCall {
  id: string;
  call_id: string;
  source: string;
  status: string;
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  transcripts: Transcript[];
  booking_confirmed: boolean;
  fare: string | null;
  eta: string | null;
  started_at: string;
  updated_at: string;
  ended_at: string | null;
  // Caller info
  caller_name: string | null;
  caller_phone: string | null;
  caller_total_bookings: number | null;
  caller_last_pickup: string | null;
  caller_last_destination: string | null;
  caller_last_booking_at: string | null;
}

// Audio playback utilities (PCM16 @ 24kHz)
const pcm16ToAudioBuffer = (ctx: AudioContext, pcmData: Uint8Array) => {
  // Interpret bytes as little-endian int16 PCM
  const int16 = new Int16Array(pcmData.buffer, pcmData.byteOffset, Math.floor(pcmData.byteLength / 2));
  const float32 = new Float32Array(int16.length);
  for (let i = 0; i < int16.length; i++) {
    float32[i] = int16[i] / 32768;
  }

  const buffer = ctx.createBuffer(1, float32.length, 24000);
  buffer.getChannelData(0).set(float32);
  return buffer;
};

class AudioQueue {
  private queue: Uint8Array[] = [];
  private isPlaying = false;
  private audioContext: AudioContext;
  private nextStartTime = 0;

  constructor(audioContext: AudioContext) {
    this.audioContext = audioContext;
  }

  addToQueue(audioData: Uint8Array) {
    this.queue.push(audioData);
    if (!this.isPlaying) {
      this.nextStartTime = this.audioContext.currentTime + 0.05; // 50ms safety buffer
      this.playNext();
    }
  }

  private playNext() {
    if (this.queue.length === 0) {
      this.isPlaying = false;
      return;
    }

    this.isPlaying = true;
    const audioData = this.queue.shift()!;

    try {
      const audioBuffer = pcm16ToAudioBuffer(this.audioContext, audioData);
      const source = this.audioContext.createBufferSource();
      source.buffer = audioBuffer;
      source.connect(this.audioContext.destination);

      const startTime = Math.max(this.nextStartTime, this.audioContext.currentTime + 0.01);
      source.start(startTime);
      this.nextStartTime = startTime + audioBuffer.duration;

      source.onended = () => this.playNext();
    } catch (error) {
      console.error('Error playing audio:', error);
      this.playNext();
    }
  }

  clear() {
    this.queue = [];
    this.isPlaying = false;
    this.nextStartTime = this.audioContext.currentTime + 0.05;
  }
}

export default function LiveCalls() {
  const [calls, setCalls] = useState<LiveCall[]>([]);
  const [selectedCall, setSelectedCall] = useState<string | null>(null);
  const [audioEnabled, setAudioEnabled] = useState(false);
  const [isListening, setIsListening] = useState(false);
  const [audioSource, setAudioSource] = useState<"ai" | "user">("ai");
  const [addressVerification, setAddressVerification] = useState(true);
  const [useTripResolver, setUseTripResolver] = useState(true);
  const [addressTtsSplicing, setAddressTtsSplicing] = useState(false);
  const [useGeminiPipeline, setUseGeminiPipeline] = useState(false);
  const [sttProvider, setSttProvider] = useState<"groq" | "deepgram">("groq");
  const [ttsProvider, setTtsProvider] = useState<"elevenlabs" | "deepgram">("elevenlabs");
  const [agents, setAgents] = useState<Agent[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<string>("ada");
  const [pickupGeocode, setPickupGeocode] = useState<GeocodeResult | null>(null);
  const [destinationGeocode, setDestinationGeocode] = useState<GeocodeResult | null>(null);
  const [tripResolveResult, setTripResolveResult] = useState<any>(null);
  
  const audioContextRef = useRef<AudioContext | null>(null);
  const audioQueueRef = useRef<AudioQueue | null>(null);
  const transcriptScrollRef = useRef<HTMLDivElement | null>(null);
  const transcriptBottomRef = useRef<HTMLDivElement | null>(null);

  // Refs to avoid stale closures in timers/realtime callbacks
  const callsRef = useRef<LiveCall[]>([]);
  const selectedCallRef = useRef<string | null>(null);
  
  // Track previous booking addresses to detect changes (for clearing stale geocode results)
  const prevBookingRef = useRef<{ pickup: string | null; destination: string | null }>({ pickup: null, destination: null });

  // Used to keep the transcript feeling "fresh" when switching calls
  const suppressAutoScrollRef = useRef(false);

  // Initialize audio context on user interaction
  const enableAudio = useCallback(() => {
    if (!audioContextRef.current) {
      audioContextRef.current = new AudioContext({ sampleRate: 24000 });
      audioQueueRef.current = new AudioQueue(audioContextRef.current);
    }
    if (audioContextRef.current.state === 'suspended') {
      audioContextRef.current.resume();
    }
    setAudioEnabled(true);
  }, []);

  const disableAudio = useCallback(() => {
    audioQueueRef.current?.clear();
    setAudioEnabled(false);
  }, []);

  const [isClearing, setIsClearing] = useState(false);

  const clearDatabase = async () => {
    setIsClearing(true);
    try {
      // Clear tables - use neq filter to delete all rows
      await supabase.from('live_call_audio').delete().neq('id', '00000000-0000-0000-0000-000000000000');
      await supabase.from('live_calls').delete().neq('id', '00000000-0000-0000-0000-000000000000');
      await supabase.from('bookings').delete().neq('id', '00000000-0000-0000-0000-000000000000');
      await supabase.from('call_logs').delete().neq('id', '00000000-0000-0000-0000-000000000000');
      await supabase.from('callers').delete().neq('id', '00000000-0000-0000-0000-000000000000');
      await supabase.from('address_cache').delete().neq('id', '00000000-0000-0000-0000-000000000000');
      
      setCalls([]);
      setSelectedCall(null);
      setPickupGeocode(null);
      setDestinationGeocode(null);
      setTripResolveResult(null);
      
      toast.success("Database cleared successfully");
    } catch (error) {
      console.error("Error clearing database:", error);
      toast.error("Failed to clear database");
    } finally {
      setIsClearing(false);
    }
  };

  // Fetch agents on mount
  useEffect(() => {
    const fetchAgents = async () => {
      const { data, error } = await supabase
        .from("agents")
        .select("id, name, slug")
        .order("name");
      if (data && !error) {
        setAgents(data);
      }
    };
    fetchAgents();
  }, []);

  useEffect(() => {
    // Keep refs in sync
    callsRef.current = calls;
    selectedCallRef.current = selectedCall;
  }, [calls, selectedCall]);

  useEffect(() => {
    // Cleanup stale active calls older than 10 minutes
    const cleanupStaleCalls = async () => {
      const tenMinutesAgo = new Date(Date.now() - 10 * 60 * 1000).toISOString();
      const { error } = await supabase
        .from("live_calls")
        .update({ status: "completed", ended_at: new Date().toISOString() })
        .eq("status", "active")
        .lt("started_at", tenMinutesAgo);

      if (error) {
        console.error("Error cleaning up stale calls:", error);
      } else {
        console.log("[LiveCalls] Cleaned up stale active calls older than 10 minutes");
      }
    };

    // Fetch initial calls on mount
    const fetchCalls = async () => {
      await cleanupStaleCalls();

      const { data, error } = await supabase
        .from("live_calls")
        .select("*")
        .in("status", ["active", "completed"])
        .order("started_at", { ascending: false })
        .limit(20);

      if (error) {
        console.error("Error fetching calls:", error);
        return;
      }

      const typedCalls = (data || []).map((call) => ({
        ...call,
        transcripts: (call.transcripts as unknown as Transcript[]) || [],
      })) as LiveCall[];

      setCalls(typedCalls);
      if (typedCalls.length > 0 && !selectedCallRef.current) {
        setSelectedCall(typedCalls[0].call_id);
      }
    };

    fetchCalls();

    // Subscribe to realtime updates - this is the PRIMARY way we get new calls
    const channel = supabase
      .channel("live-calls-monitor")
      .on(
        "postgres_changes",
        { event: "*", schema: "public", table: "live_calls" },
        (payload) => {
          console.log("Live call update:", payload);

          if (payload.eventType === "INSERT") {
            const newCall = {
              ...payload.new,
              transcripts: (payload.new.transcripts as unknown as Transcript[]) || [],
            } as LiveCall;
            setCalls((prev) => [newCall, ...prev.slice(0, 19)]);
            setSelectedCall(newCall.call_id); // Auto-select new call
          } else if (payload.eventType === "UPDATE") {
            setCalls((prev) =>
              prev.map((call) =>
                call.call_id === payload.new.call_id
                  ? ({
                      ...payload.new,
                      transcripts: (payload.new.transcripts as unknown as Transcript[]) || [],
                    } as LiveCall)
                  : call
              )
            );
          } else if (payload.eventType === "DELETE") {
            setCalls((prev) => prev.filter((call) => call.id !== payload.old.id));
          }
        }
      )
      .subscribe((status) => {
        console.log("[LiveCalls] Realtime subscription status:", status);
      });

    // Fallback: re-fetch only when tab regains focus (not on interval)
    const handleVisibility = () => {
      if (document.visibilityState === "visible") {
        console.log("[LiveCalls] Tab visible again, refreshing calls");
        fetchCalls();
      }
    };
    document.addEventListener("visibilitychange", handleVisibility);

    return () => {
      document.removeEventListener("visibilitychange", handleVisibility);
      supabase.removeChannel(channel);
    };
  }, []);

  // Subscribe to audio stream for selected call
  useEffect(() => {
    if (!selectedCall || !audioEnabled) {
      setIsListening(false);
      return;
    }

    console.log(`[LiveCalls] Subscribing to ${audioSource} audio for call: ${selectedCall}`);
    setIsListening(true);

    const audioChannel = supabase
      .channel(`audio-${selectedCall}-${audioSource}`)
      .on(
        "postgres_changes",
        {
          event: "INSERT",
          schema: "public",
          table: "live_call_audio",
          filter: `call_id=eq.${selectedCall}`
        },
        (payload) => {
          // Filter by audio source
          const payloadSource = (payload.new as any).audio_source as string;
          if (payloadSource !== audioSource) return;
          
          const audioChunk = payload.new.audio_chunk as string;
          if (audioChunk && audioQueueRef.current) {
            // Convert base64 to Uint8Array
            const binaryString = atob(audioChunk);
            const bytes = new Uint8Array(binaryString.length);
            for (let i = 0; i < binaryString.length; i++) {
              bytes[i] = binaryString.charCodeAt(i);
            }
            audioQueueRef.current.addToQueue(bytes);
          }
        }
      )
      .subscribe();

    return () => {
      console.log(`[LiveCalls] Unsubscribing from audio for call: ${selectedCall}`);
      supabase.removeChannel(audioChannel);
      audioQueueRef.current?.clear();
      setIsListening(false);
    };
  }, [selectedCall, audioEnabled, audioSource]);

  const inferCityHintFromAddress = (address?: string | null): string | undefined => {
    if (!address) return undefined;
    const lower = address.toLowerCase();

    const knownCities = [
      "coventry",
      "birmingham",
      "solihull",
      "warwick",
      "leamington",
      "kenilworth",
      "nuneaton",
      "rugby",
    ];

    for (const city of knownCities) {
      if (lower.includes(city)) {
        return city.charAt(0).toUpperCase() + city.slice(1);
      }
    }

    return undefined;
  };

  // Geocode addresses when they change and verification is enabled
  useEffect(() => {
    if (!addressVerification || !selectedCall) {
      setPickupGeocode(null);
      setDestinationGeocode(null);
      setTripResolveResult(null);
      prevBookingRef.current = { pickup: null, destination: null };
      return;
    }

    const callData = calls.find(c => c.call_id === selectedCall);
    if (!callData) return;

    // Detect when booking addresses change (e.g., new booking started after cancellation)
    const prevPickup = prevBookingRef.current.pickup;
    const prevDestination = prevBookingRef.current.destination;
    const currentPickup = callData.pickup;
    const currentDestination = callData.destination;
    
    // Clear stale geocode results when addresses change significantly
    const pickupChanged = prevPickup !== currentPickup;
    const destinationChanged = prevDestination !== currentDestination;
    
    if (pickupChanged || destinationChanged) {
      console.log("[LiveCalls] Booking addresses changed - clearing stale geocode results");
      if (pickupChanged) setPickupGeocode(null);
      if (destinationChanged) setDestinationGeocode(null);
      if (pickupChanged || destinationChanged) setTripResolveResult(null);
      prevBookingRef.current = { pickup: currentPickup, destination: currentDestination };
    }

    // Use taxi-trip-resolve if enabled, otherwise fallback to basic geocode
    if (useTripResolver) {
      const resolveTripAddresses = async () => {
        if (!callData.pickup && !callData.destination) {
          setTripResolveResult(null);
          setPickupGeocode(null);
          setDestinationGeocode(null);
          return;
        }

        // Set loading states
        if (callData.pickup) {
          setPickupGeocode({ found: false, address: callData.pickup, loading: true });
        }
        if (callData.destination) {
          setDestinationGeocode({ found: false, address: callData.destination, loading: true });
        }

        try {
          const cityHint =
            inferCityHintFromAddress(callData.pickup) ||
            inferCityHintFromAddress(callData.caller_last_pickup) ||
            inferCityHintFromAddress(callData.destination) ||
            inferCityHintFromAddress(callData.caller_last_destination) ||
            "Coventry";

          const { data, error } = await supabase.functions.invoke("taxi-trip-resolve", {
            body: {
              pickup_input: callData.pickup || undefined,
              dropoff_input: callData.destination || undefined,
              caller_city_hint: cityHint,
              passengers: callData.passengers || 1,
              country: "GB"
            }
          });

          if (error) {
            console.error("Trip resolve error:", error);
            setTripResolveResult({ ok: false, error: error.message });
            if (callData.pickup) setPickupGeocode({ found: false, address: callData.pickup, error: error.message });
            if (callData.destination) setDestinationGeocode({ found: false, address: callData.destination, error: error.message });
            return;
          }

          console.log("Trip resolve result:", data);
          setTripResolveResult(data);

          // Map to geocode result format for UI display
          if (data.pickup) {
            setPickupGeocode({
              found: true,
              address: callData.pickup!,
              display_name: data.pickup.formatted_address,
              place_name: data.pickup.name, // Business name from Google
              lat: data.pickup.lat,
              lon: data.pickup.lng
            });
          } else if (data.needs_pickup_disambiguation && data.pickup_matches) {
            // Multiple areas found - disambiguation needed
            const areas = data.pickup_matches.map((m: any) => m.area || m.locality || m.city).filter(Boolean);
            setPickupGeocode({
              found: false,
              address: callData.pickup!,
              needs_disambiguation: true,
              disambiguation_areas: areas,
              error: "Disambiguation needed"
            });
          } else if (callData.pickup) {
            setPickupGeocode({ found: false, address: callData.pickup, error: "Not found" });
          }

          if (data.dropoff) {
            setDestinationGeocode({
              found: true,
              address: callData.destination!,
              display_name: data.dropoff.formatted_address,
              place_name: data.dropoff.name, // Business name from Google
              lat: data.dropoff.lat,
              lon: data.dropoff.lng
            });
          } else if (data.needs_dropoff_disambiguation && data.dropoff_matches) {
            // Multiple areas found - disambiguation needed
            const areas = data.dropoff_matches.map((m: any) => m.area || m.locality || m.city).filter(Boolean);
            setDestinationGeocode({
              found: false,
              address: callData.destination!,
              needs_disambiguation: true,
              disambiguation_areas: areas,
              error: "Disambiguation needed"
            });
          } else if (callData.destination) {
            setDestinationGeocode({ found: false, address: callData.destination, error: "Not found" });
          }
        } catch (err) {
          console.error("Trip resolve exception:", err);
          setTripResolveResult({ ok: false, error: "Request failed" });
        }
      };

      resolveTripAddresses();
    } else {
      // Use basic geocode function
      setTripResolveResult(null);
      
      const geocodeAddress = async (address: string, setter: (r: GeocodeResult) => void) => {
        if (!address) {
          setter({ found: false, address: "", error: "No address" });
          return;
        }
        
        setter({ found: false, address, loading: true });
        
        try {
          const { data, error } = await supabase.functions.invoke("geocode", {
            body: { address, country: "UK" }
          });
          
          if (error) {
            setter({ found: false, address, error: error.message });
          } else {
            setter(data);
          }
        } catch (err) {
          setter({ found: false, address, error: "Failed to verify" });
        }
      };

      // Geocode pickup if present
      if (callData.pickup) {
        geocodeAddress(callData.pickup, setPickupGeocode);
      } else {
        setPickupGeocode(null);
      }

      // Geocode destination if present
      if (callData.destination) {
        geocodeAddress(callData.destination, setDestinationGeocode);
      } else {
        setDestinationGeocode(null);
      }
    }
  }, [selectedCall, addressVerification, useTripResolver, calls]);

  const selectedCallData = calls.find(c => c.call_id === selectedCall);
  const activeCalls = calls.filter(c => c.status === "active");

  // When switching to a new call, reset the transcript scroll so the log feels "fresh"
  useEffect(() => {
    suppressAutoScrollRef.current = true;

    requestAnimationFrame(() => {
      transcriptScrollRef.current?.scrollTo({ top: 0, behavior: "auto" });
    });

    const t = window.setTimeout(() => {
      suppressAutoScrollRef.current = false;
    }, 400);

    return () => window.clearTimeout(t);
  }, [selectedCall]);

  // Auto-scroll transcript to the latest message (but not immediately after switching calls)
  useEffect(() => {
    if (!selectedCallData) return;
    if (suppressAutoScrollRef.current) return;

    requestAnimationFrame(() => {
      transcriptBottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
    });
  }, [selectedCall, selectedCallData?.transcripts?.length]);

  const formatTime = (dateStr: string, opts?: { ms?: boolean }) => {
    const date = new Date(dateStr);
    const base = date.toLocaleTimeString("en-GB", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });

    if (!opts?.ms) return base;

    const ms = date.getMilliseconds().toString().padStart(3, "0");
    return `${base}.${ms}`;
  };

  const getDuration = (startedAt: string, endedAt?: string | null) => {
    const start = new Date(startedAt).getTime();
    const end = endedAt ? new Date(endedAt).getTime() : Date.now();
    const seconds = Math.floor((end - start) / 1000);
    const mins = Math.floor(seconds / 60);
    const secs = seconds % 60;
    return `${mins}:${secs.toString().padStart(2, "0")}`;
  };

  return (
    <div className="min-h-screen bg-gradient-dark p-6">
      <div className="max-w-7xl mx-auto">
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-3">
            <Button variant="ghost" size="icon" asChild className="mr-2">
              <Link to="/">
                <ArrowLeft className="w-5 h-5" />
              </Link>
            </Button>
            <Radio className="w-8 h-8 text-primary animate-pulse" />
            <h1 className="text-3xl font-display font-bold text-primary">Live Asterisk Streams</h1>
          </div>
          <div className="flex items-center gap-4">
            {/* Agent Selector */}
            <div className="flex items-center gap-2">
              <Bot className="w-4 h-4 text-muted-foreground" />
              <Select value={selectedAgent} onValueChange={setSelectedAgent}>
                <SelectTrigger className="w-32 h-8 text-sm bg-card border-border">
                  <SelectValue placeholder="Select Agent" />
                </SelectTrigger>
                <SelectContent className="bg-popover border-border">
                  {agents.map((agent) => (
                    <SelectItem key={agent.id} value={agent.slug}>
                      {agent.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {/* Pipeline Selector */}
            <div className="flex items-center gap-2 bg-card/50 rounded-lg px-3 py-1.5 border border-border">
              <div className="flex flex-col items-center">
                <span className={`text-xs font-bold ${!useGeminiPipeline ? 'text-primary' : 'text-muted-foreground'}`}>
                  OpenAI
                </span>
                <span className={`text-[10px] ${!useGeminiPipeline ? 'text-primary/70' : 'text-muted-foreground/50'}`}>
                  Realtime
                </span>
              </div>
              <Switch
                id="pipeline-select"
                checked={useGeminiPipeline}
                onCheckedChange={setUseGeminiPipeline}
              />
              <div className="flex flex-col items-center">
                <span className={`text-xs font-bold ${useGeminiPipeline ? 'text-green-400' : 'text-muted-foreground'}`}>
                  Gemini
                </span>
                <span className={`text-[10px] ${useGeminiPipeline ? 'text-green-400/70' : 'text-muted-foreground/50'}`}>
                  {sttProvider === "deepgram" ? "Deepgram" : "Groq"} STT
                </span>
              </div>
            </div>
            {/* STT Provider Selector (only when Gemini pipeline active) */}
            {useGeminiPipeline && (
              <div className="flex items-center gap-2 bg-card/50 rounded-lg px-3 py-1.5 border border-border">
                <span className={`text-xs font-medium ${sttProvider === "groq" ? 'text-amber-400' : 'text-muted-foreground'}`}>
                  Groq
                </span>
                <Switch
                  id="stt-provider"
                  checked={sttProvider === "deepgram"}
                  onCheckedChange={(checked) => setSttProvider(checked ? "deepgram" : "groq")}
                />
                <span className={`text-xs font-medium ${sttProvider === "deepgram" ? 'text-cyan-400' : 'text-muted-foreground'}`}>
                  Deepgram
                </span>
              </div>
            )}
            {/* TTS Provider Selector (only when Gemini pipeline active) */}
            {useGeminiPipeline && (
              <div className="flex items-center gap-2 bg-card/50 rounded-lg px-3 py-1.5 border border-border">
                <span className={`text-xs font-medium ${ttsProvider === "elevenlabs" ? 'text-purple-400' : 'text-muted-foreground'}`}>
                  11Labs
                </span>
                <Switch
                  id="tts-provider"
                  checked={ttsProvider === "deepgram"}
                  onCheckedChange={(checked) => setTtsProvider(checked ? "deepgram" : "elevenlabs")}
                />
                <span className={`text-xs font-medium ${ttsProvider === "deepgram" ? 'text-cyan-400' : 'text-muted-foreground'}`}>
                  Deepgram
                </span>
              </div>
            )}
            {/* Address TTS Splicing Toggle */}
            <div className="flex items-center gap-2">
              <Switch
                id="address-tts"
                checked={addressTtsSplicing}
                onCheckedChange={setAddressTtsSplicing}
              />
              <label htmlFor="address-tts" className="text-sm text-muted-foreground cursor-pointer">
                Address TTS
              </label>
            </div>
            {/* Address Verification Toggle */}
            <div className="flex items-center gap-2">
              <Switch
                id="address-verify"
                checked={addressVerification}
                onCheckedChange={setAddressVerification}
              />
              <label htmlFor="address-verify" className="text-sm text-muted-foreground cursor-pointer">
                Verify Addresses
              </label>
            </div>
            {/* Trip Resolver Toggle (uses taxi-trip-resolve function) */}
            {addressVerification && (
              <div className="flex items-center gap-2 bg-card/50 rounded-lg px-3 py-1.5 border border-border">
                <span className={`text-xs font-medium ${!useTripResolver ? 'text-primary' : 'text-muted-foreground'}`}>
                  Basic
                </span>
                <Switch
                  id="trip-resolver"
                  checked={useTripResolver}
                  onCheckedChange={setUseTripResolver}
                />
                <span className={`text-xs font-medium ${useTripResolver ? 'text-green-400' : 'text-muted-foreground'}`}>
                  Trip Resolver
                </span>
              </div>
            )}
            {/* Audio controls */}
            <div className="flex items-center gap-2">
              <Button
                variant={audioEnabled ? "default" : "outline"}
                size="sm"
                onClick={audioEnabled ? disableAudio : enableAudio}
                className={audioEnabled ? "bg-green-600 hover:bg-green-700" : ""}
              >
                {audioEnabled ? (
                  <>
                    <Volume2 className="w-4 h-4 mr-2" />
                    {isListening ? "Listening..." : "Audio On"}
                  </>
                ) : (
                  <>
                    <VolumeX className="w-4 h-4 mr-2" />
                    Enable Audio
                  </>
                )}
              </Button>
              {audioEnabled && (
                <Select value={audioSource} onValueChange={(v) => setAudioSource(v as "ai" | "user")}>
                  <SelectTrigger className="w-[120px] h-8">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="ai">
                      <div className="flex items-center gap-2">
                        <Bot className="w-3 h-3" />
                        Ada
                      </div>
                    </SelectItem>
                    <SelectItem value="user">
                      <div className="flex items-center gap-2">
                        <User className="w-3 h-3" />
                        Caller
                      </div>
                    </SelectItem>
                  </SelectContent>
                </Select>
              )}
            </div>
            <Badge variant="outline" className="text-green-400 border-green-400">
              <span className="w-2 h-2 bg-green-400 rounded-full mr-2 animate-pulse" />
              {activeCalls.length} Active
            </Badge>
            <Badge variant="outline" className="text-muted-foreground">
              {calls.length} Total
            </Badge>
            {/* Clear Database Button */}
            <AlertDialog>
              <AlertDialogTrigger asChild>
                <Button variant="destructive" size="sm" disabled={isClearing}>
                  <Trash2 className="w-4 h-4 mr-2" />
                  {isClearing ? "Clearing..." : "Clear DB"}
                </Button>
              </AlertDialogTrigger>
              <AlertDialogContent>
                <AlertDialogHeader>
                  <AlertDialogTitle>Clear Database?</AlertDialogTitle>
                  <AlertDialogDescription>
                    This will permanently delete all calls, bookings, callers, and call logs. This action cannot be undone.
                  </AlertDialogDescription>
                </AlertDialogHeader>
                <AlertDialogFooter>
                  <AlertDialogCancel>Cancel</AlertDialogCancel>
                  <AlertDialogAction onClick={clearDatabase} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">
                    Clear Everything
                  </AlertDialogAction>
                </AlertDialogFooter>
              </AlertDialogContent>
            </AlertDialog>
          </div>
        </div>

        {/* Pipeline Status Bar */}
        <div className="mb-4 p-3 rounded-lg bg-card/50 border border-border flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className={`w-2 h-2 rounded-full ${useGeminiPipeline ? 'bg-green-400' : 'bg-primary'} animate-pulse`} />
            <span className="text-sm font-medium">
              {useGeminiPipeline ? 'Gemini Pipeline' : 'OpenAI Realtime'}
            </span>
            {useGeminiPipeline ? (
              <>
                <Badge variant="outline" className={`text-xs ${sttProvider === "deepgram" ? 'text-cyan-400 border-cyan-400/50' : 'text-amber-400 border-amber-400/50'}`}>
                  {sttProvider === "deepgram" ? 'Deepgram Nova' : 'Groq Whisper'} STT
                </Badge>
                <Badge variant="outline" className="text-xs text-green-400 border-green-400/50">
                  Gemini LLM
                </Badge>
                <Badge variant="outline" className={`text-xs ${ttsProvider === "deepgram" ? 'text-cyan-400 border-cyan-400/50' : 'text-purple-400 border-purple-400/50'}`}>
                  {ttsProvider === "deepgram" ? 'Deepgram Aura' : 'ElevenLabs'} TTS
                </Badge>
              </>
            ) : (
              <>
                <Badge variant="outline" className="text-xs text-primary border-primary/50">
                  Whisper-1 STT
                </Badge>
                <Badge variant="outline" className="text-xs text-primary border-primary/50">
                  GPT-4o Realtime
                </Badge>
              </>
            )}
            <Badge variant="outline" className="text-xs">
              {useGeminiPipeline ? 'FREE LLM' : '~$2.40/M tokens'}
            </Badge>
            <Badge variant="outline" className="text-xs">
              {useGeminiPipeline 
                ? (sttProvider === "deepgram" && ttsProvider === "deepgram" 
                    ? '~400-600ms (full Deepgram)' 
                    : sttProvider === "deepgram" 
                      ? '~500-800ms' 
                      : '~600-1000ms') 
                : '~400-500ms'}
            </Badge>
          </div>
          <code className="text-xs bg-muted px-2 py-1 rounded font-mono text-muted-foreground">
            {useGeminiPipeline 
              ? 'taxi-realtime-gemini'
              : 'taxi-realtime'
            }
          </code>
        </div>

        <div className="grid grid-cols-12 gap-6">
          {/* Call List */}
          <div className="col-span-4 space-y-3">
            <h2 className="text-sm font-semibold text-muted-foreground uppercase tracking-wider mb-3">
              Active Calls
            </h2>
            
            {calls.length === 0 ? (
              <Card className="p-8 text-center">
                <PhoneOff className="w-12 h-12 mx-auto text-muted-foreground mb-3" />
                <p className="text-muted-foreground">No active calls</p>
                <p className="text-sm text-muted-foreground/60 mt-1">
                  Calls from Asterisk will appear here in real-time
                </p>
              </Card>
            ) : (
              calls.map((call) => (
                <Card
                  key={call.id}
                  onClick={() => setSelectedCall(call.call_id)}
                  className={`p-4 cursor-pointer transition-all hover:border-primary/50 ${
                    selectedCall === call.call_id ? "border-primary bg-primary/5" : ""
                  }`}
                >
                  <div className="flex items-start justify-between">
                    <div className="flex items-center gap-3">
                      <div className={`p-2 rounded-full ${
                        call.status === "active" 
                          ? "bg-green-500/20 text-green-400" 
                          : "bg-muted text-muted-foreground"
                      }`}>
                        <Phone className="w-4 h-4" />
                      </div>
                      <div>
                        <p className="font-mono text-sm">
                          {call.source === "asterisk" ? "📞" : "🌐"} {call.call_id.slice(0, 20)}...
                        </p>
                        <p className="text-xs text-muted-foreground">
                          {formatTime(call.started_at)} • {getDuration(call.started_at, call.ended_at)}
                        </p>
                      </div>
                    </div>
                    <div className="flex flex-col items-end gap-1">
                      <Badge 
                        variant={call.status === "active" ? "default" : "secondary"}
                        className={call.status === "active" ? "bg-green-600" : ""}
                      >
                        {call.status}
                      </Badge>
                      {call.booking_confirmed && (
                        <Badge variant="outline" className="text-primary border-primary text-xs">
                          Booked
                        </Badge>
                      )}
                    </div>
                  </div>

                  {/* Booking preview */}
                  {(call.pickup || call.destination) && (
                    <div className="mt-3 pt-3 border-t border-border/50 text-xs space-y-1">
                      {call.pickup && (
                        <p className="flex items-center gap-2 text-muted-foreground">
                          <MapPin className="w-3 h-3 text-green-400" />
                          <span className="truncate">{call.pickup}</span>
                        </p>
                      )}
                      {call.destination && (
                        <p className="flex items-center gap-2 text-muted-foreground">
                          <MapPin className="w-3 h-3 text-red-400" />
                          <span className="truncate">{call.destination}</span>
                        </p>
                      )}
                    </div>
                  )}
                </Card>
              ))
            )}
          </div>

          {/* Call Details & Transcript */}
          <div className="col-span-8">
            {selectedCallData ? (
              <Card className="h-[calc(100vh-40px)] flex flex-col">
                {/* Call Header */}
                <div className="p-4 border-b border-border">
                  <div className="flex items-center justify-between">
                    <div>
                      <h3 className="font-semibold text-lg">
                        {selectedCallData.source === "asterisk" ? "📞 Asterisk Call" : "🌐 Web Call"}
                      </h3>
                      <p className="text-sm font-mono text-muted-foreground">{selectedCallData.call_id}</p>
                    </div>
                    <div className="flex items-center gap-4">
                      <div className="text-right">
                        <p className="text-2xl font-bold font-mono">
                          {getDuration(selectedCallData.started_at, selectedCallData.ended_at)}
                        </p>
                        <p className="text-xs text-muted-foreground">Duration</p>
                      </div>
                      <Badge 
                        variant={selectedCallData.status === "active" ? "default" : "secondary"}
                        className={`h-8 px-4 ${selectedCallData.status === "active" ? "bg-green-600 animate-pulse" : ""}`}
                      >
                        {selectedCallData.status === "active" ? "🔴 LIVE" : selectedCallData.status}
                      </Badge>
                    </div>
                  </div>

                  {/* Caller Info Card */}
                  {(selectedCallData.caller_name || selectedCallData.caller_phone) && (
                    <div className="mt-4 p-3 bg-muted/50 rounded-lg border border-border">
                      <div className="flex items-center gap-3">
                        <div className="p-2 rounded-full bg-primary/20">
                          <User className="w-5 h-5 text-primary" />
                        </div>
                        <div className="flex-1">
                          <div className="flex items-center gap-2">
                            <p className="font-semibold text-lg">
                              {selectedCallData.caller_name || "Unknown Caller"}
                            </p>
                            {selectedCallData.caller_total_bookings && selectedCallData.caller_total_bookings > 0 && (
                              <Badge variant="outline" className="text-xs">
                                {selectedCallData.caller_total_bookings} {selectedCallData.caller_total_bookings === 1 ? "booking" : "bookings"}
                              </Badge>
                            )}
                          </div>
                          {selectedCallData.caller_phone && (
                            <p className="text-sm text-muted-foreground font-mono">
                              +{selectedCallData.caller_phone}
                            </p>
                          )}
                        </div>
                      </div>
                      
                      {/* Last Trip */}
                      {selectedCallData.caller_last_pickup && (
                        <div className="mt-3 pt-3 border-t border-border/50">
                          <div className="flex items-center justify-between text-xs text-muted-foreground mb-2">
                            <div className="flex items-center gap-2">
                              <History className="w-3 h-3" />
                              <span>Last Trip</span>
                            </div>
                            {selectedCallData.caller_last_booking_at && (
                              <span className="text-muted-foreground/70">
                                {new Date(selectedCallData.caller_last_booking_at).toLocaleDateString('en-GB', {
                                  day: 'numeric',
                                  month: 'short',
                                  year: 'numeric',
                                  hour: '2-digit',
                                  minute: '2-digit'
                                })}
                              </span>
                            )}
                          </div>
                          <div className="text-sm space-y-1">
                            <p className="flex items-center gap-2">
                              <MapPin className="w-3 h-3 text-green-400" />
                              <span className="truncate">{selectedCallData.caller_last_pickup}</span>
                            </p>
                            {selectedCallData.caller_last_destination && (
                              <p className="flex items-center gap-2">
                                <MapPin className="w-3 h-3 text-red-400" />
                                <span className="truncate">{selectedCallData.caller_last_destination}</span>
                              </p>
                            )}
                          </div>
                        </div>
                      )}
                    </div>
                  )}

                  {/* Booking Info */}
                  {selectedCallData.booking_confirmed && (
                    <div className="mt-4 p-3 bg-primary/10 rounded-lg border border-primary/30">
                      <p className="text-sm font-semibold text-primary mb-2">✅ Booking Confirmed</p>
                      <div className="grid grid-cols-2 gap-4 text-sm">
                        {/* Pickup with verification */}
                        <div className="space-y-1 min-h-[60px]">
                          <div className="flex items-center gap-2">
                            <MapPin className="w-4 h-4 text-green-400" />
                            <span className="font-medium">Pickup</span>
                            <div className="w-4 h-4 flex items-center justify-center">
                              {addressVerification && pickupGeocode && (
                                pickupGeocode.loading ? (
                                  <Loader2 className="w-3 h-3 animate-spin text-muted-foreground" />
                                ) : pickupGeocode.found ? (
                                  <CheckCircle2 className="w-4 h-4 text-green-500" />
                                ) : pickupGeocode.needs_disambiguation ? (
                                  <AlertCircle className="w-4 h-4 text-amber-500" />
                                ) : (
                                  <XCircle className="w-4 h-4 text-red-500" />
                                )
                              )}
                            </div>
                          </div>
                          <p className="text-muted-foreground pl-6">{selectedCallData.pickup || "—"}</p>
                          <p className={`text-xs pl-6 min-h-[16px] ${
                            !addressVerification || !pickupGeocode || pickupGeocode.loading
                              ? "text-transparent"
                              : pickupGeocode.found 
                                ? "text-green-400" 
                                : pickupGeocode.needs_disambiguation 
                                  ? "text-amber-400" 
                                  : "text-red-400"
                          }`}>
                            {addressVerification && pickupGeocode && !pickupGeocode.loading
                              ? (pickupGeocode.found 
                                  ? `✓ ${pickupGeocode.place_name ? `${pickupGeocode.place_name} - ` : ""}${pickupGeocode.display_name?.split(",").slice(0, 3).join(",")}`
                                  : pickupGeocode.needs_disambiguation
                                    ? `⚠ Disambiguation needed${pickupGeocode.disambiguation_areas?.length ? `: ${pickupGeocode.disambiguation_areas.slice(0, 3).join(", ")}` : ""}`
                                    : `✗ ${pickupGeocode.error || "Not found"}`)
                              : "—"}
                          </p>
                        </div>
                        
                        {/* Destination with verification */}
                        <div className="space-y-1 min-h-[60px]">
                          <div className="flex items-center gap-2">
                            <MapPin className="w-4 h-4 text-red-400" />
                            <span className="font-medium">Destination</span>
                            <div className="w-4 h-4 flex items-center justify-center">
                              {addressVerification && destinationGeocode && (
                                destinationGeocode.loading ? (
                                  <Loader2 className="w-3 h-3 animate-spin text-muted-foreground" />
                                ) : destinationGeocode.found ? (
                                  <CheckCircle2 className="w-4 h-4 text-green-500" />
                                ) : destinationGeocode.needs_disambiguation ? (
                                  <AlertCircle className="w-4 h-4 text-amber-500" />
                                ) : (
                                  <XCircle className="w-4 h-4 text-red-500" />
                                )
                              )}
                            </div>
                          </div>
                          <p className="text-muted-foreground pl-6">{selectedCallData.destination || "—"}</p>
                          <p className={`text-xs pl-6 min-h-[16px] ${
                            !addressVerification || !destinationGeocode || destinationGeocode.loading
                              ? "text-transparent"
                              : destinationGeocode.found 
                                ? "text-green-400" 
                                : destinationGeocode.needs_disambiguation 
                                  ? "text-amber-400" 
                                  : "text-red-400"
                          }`}>
                            {addressVerification && destinationGeocode && !destinationGeocode.loading
                              ? (destinationGeocode.found 
                                  ? `✓ ${destinationGeocode.place_name ? `${destinationGeocode.place_name} - ` : ""}${destinationGeocode.display_name?.split(",").slice(0, 3).join(",")}`
                                  : destinationGeocode.needs_disambiguation
                                    ? `⚠ Disambiguation needed${destinationGeocode.disambiguation_areas?.length ? `: ${destinationGeocode.disambiguation_areas.slice(0, 3).join(", ")}` : ""}`
                                    : `✗ ${destinationGeocode.error || "Not found"}`)
                              : "—"}
                          </p>
                        </div>
                      </div>
                      
                      {/* Passengers and Fare */}
                      <div className="grid grid-cols-2 gap-4 text-sm mt-3 pt-3 border-t border-primary/20">
                        <div className="flex items-center gap-2">
                          <Users className="w-4 h-4" />
                          <span>{selectedCallData.passengers || "—"} passengers</span>
                        </div>
                        <div className="flex items-center gap-2">
                          <DollarSign className="w-4 h-4 text-primary" />
                          <span>{selectedCallData.fare || "—"}</span>
                        </div>
                      </div>

                      {/* Trip Resolver Results */}
                      {addressVerification && useTripResolver && tripResolveResult?.ok && (
                        <div className="mt-3 pt-3 border-t border-primary/20 space-y-2">
                          <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wider flex items-center gap-2">
                            📍 Trip Resolver
                            {tripResolveResult.inferred_area?.city && (
                              <Badge variant="outline" className="text-xs">
                                {tripResolveResult.inferred_area.city} ({tripResolveResult.inferred_area.confidence})
                              </Badge>
                            )}
                          </p>
                          <div className="grid grid-cols-3 gap-3 text-sm">
                            {tripResolveResult.distance && (
                              <div className="bg-muted/50 rounded p-2 text-center">
                                <p className="text-xs text-muted-foreground">Distance</p>
                                <p className="font-semibold">{tripResolveResult.distance.miles} mi</p>
                                <p className="text-xs text-muted-foreground">{tripResolveResult.distance.duration_text}</p>
                              </div>
                            )}
                            {tripResolveResult.fare_estimate && (
                              <div className="bg-muted/50 rounded p-2 text-center">
                                <p className="text-xs text-muted-foreground">Est. Fare</p>
                                <p className="font-semibold text-primary">£{tripResolveResult.fare_estimate.amount}</p>
                                <p className="text-xs text-muted-foreground">
                                  £{tripResolveResult.fare_estimate.breakdown.base} + £{tripResolveResult.fare_estimate.breakdown.per_mile_rate}/mi
                                </p>
                              </div>
                            )}
                            {tripResolveResult.pickup?.city && tripResolveResult.dropoff?.city && (
                              <div className="bg-muted/50 rounded p-2 text-center">
                                <p className="text-xs text-muted-foreground">Route</p>
                                <p className="font-semibold text-xs">
                                  {tripResolveResult.pickup.city} → {tripResolveResult.dropoff.city}
                                </p>
                              </div>
                            )}
                          </div>
                        </div>
                      )}
                    </div>
                  )}
                </div>

                {/* Live Transcript */}
                <div
                  key={selectedCall || "no-call"}
                  ref={transcriptScrollRef}
                  className="flex-1 overflow-y-auto p-4 flex flex-col"
                >
                  <div className="space-y-3">
                    {selectedCallData.transcripts.length === 0 ? (
                      <div className="text-center py-12 text-muted-foreground">
                        <Clock className="w-8 h-8 mx-auto mb-2 animate-spin" />
                        <p>Waiting for conversation...</p>
                      </div>
                    ) : (
                      selectedCallData.transcripts
                        .map((t, idx) => ({ t, idx }))
                        .sort((a, b) => {
                          const ta = new Date(a.t.timestamp).getTime();
                          const tb = new Date(b.t.timestamp).getTime();
                          return ta === tb ? a.idx - b.idx : ta - tb;
                        })
                        .map(({ t }, i) => (
                          <div
                           key={i}
                           className={`flex ${
                            t.role === "user" 
                              ? "justify-end" 
                              : t.role === "system" 
                                ? "justify-center" 
                                : "justify-start"
                          }`}
                        >
                          {t.role === "system" ? (
                            <div className="max-w-[90%] px-3 py-1.5 rounded-lg bg-amber-500/10 border border-amber-500/30 text-amber-400">
                              <p className="text-xs font-mono">{t.text}</p>
                              <p className="text-xs opacity-60 mt-0.5 text-center">
                                {formatTime(t.timestamp, { ms: true })}
                              </p>
                            </div>
                          ) : (
                            <div
                              className={`max-w-[80%] p-3 rounded-xl ${
                                t.role === "user"
                                  ? "bg-primary text-primary-foreground"
                                  : "bg-muted"
                              }`}
                            >
                              <p className="text-sm">{t.text}</p>
                              <p className="text-xs opacity-60 mt-1">
                                {t.role === "user" ? "Customer" : "Ada"} • {formatTime(t.timestamp, { ms: true })}
                              </p>
                            </div>
                          )}
                        </div>
                      ))
                    )}
                  </div>

                  {/* Auto-scroll anchor */}
                  <div ref={transcriptBottomRef} />
                </div>
              </Card>
            ) : (
              <Card className="h-[calc(100vh-40px)] flex items-center justify-center">
                <div className="text-center">
                  <Radio className="w-16 h-16 mx-auto text-muted-foreground mb-4" />
                  <p className="text-xl font-semibold text-muted-foreground">Select a call to monitor</p>
                  <p className="text-sm text-muted-foreground/60 mt-1">
                    Or wait for incoming Asterisk calls
                  </p>
                </div>
              </Card>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
