import { useEffect, useState, useRef, useCallback } from "react";
import { Link } from "react-router-dom";
import { supabase } from "@/integrations/supabase/client";
import { Card } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Phone, PhoneOff, MapPin, Users, Clock, DollarSign, Radio, Volume2, VolumeX, ArrowLeft, CheckCircle2, XCircle, Loader2, User, History } from "lucide-react";

interface Transcript {
  role: string;
  text: string;
  timestamp: string;
}

interface GeocodeResult {
  found: boolean;
  address: string;
  display_name?: string;
  lat?: number;
  lon?: number;
  error?: string;
  loading?: boolean;
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
  const [addressVerification, setAddressVerification] = useState(true);
  const [useTripResolver, setUseTripResolver] = useState(true);
  const [addressTtsSplicing, setAddressTtsSplicing] = useState(false);
  const [useGeminiPipeline, setUseGeminiPipeline] = useState(false);
  const [pickupGeocode, setPickupGeocode] = useState<GeocodeResult | null>(null);
  const [destinationGeocode, setDestinationGeocode] = useState<GeocodeResult | null>(null);
  const [tripResolveResult, setTripResolveResult] = useState<any>(null);
  
  const audioContextRef = useRef<AudioContext | null>(null);
  const audioQueueRef = useRef<AudioQueue | null>(null);

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

  useEffect(() => {
    // Fetch initial active calls
    const fetchCalls = async () => {
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

      // Cast the transcripts field properly
      const typedCalls = (data || []).map(call => ({
        ...call,
        transcripts: (call.transcripts as unknown as Transcript[]) || []
      }));
      setCalls(typedCalls);
      if (typedCalls.length > 0 && !selectedCall) {
        setSelectedCall(typedCalls[0].call_id);
      }
    };

    fetchCalls();

    // Subscribe to realtime updates
    const channel = supabase
      .channel("live-calls-monitor")
      .on(
        "postgres_changes",
        {
          event: "*",
          schema: "public",
          table: "live_calls"
        },
        (payload) => {
          console.log("Live call update:", payload);
          
          if (payload.eventType === "INSERT") {
            const newCall = {
              ...payload.new,
              transcripts: (payload.new.transcripts as unknown as Transcript[]) || []
            } as LiveCall;
            setCalls(prev => [newCall, ...prev.slice(0, 19)]);
            setSelectedCall(newCall.call_id);
          } else if (payload.eventType === "UPDATE") {
            setCalls(prev => prev.map(call => 
              call.call_id === payload.new.call_id 
                ? { 
                    ...payload.new, 
                    transcripts: (payload.new.transcripts as unknown as Transcript[]) || [] 
                  } as LiveCall
                : call
            ));
          } else if (payload.eventType === "DELETE") {
            setCalls(prev => prev.filter(call => call.id !== payload.old.id));
          }
        }
      )
      .subscribe();

    return () => {
      supabase.removeChannel(channel);
    };
  }, []);

  // Subscribe to audio stream for selected call
  useEffect(() => {
    if (!selectedCall || !audioEnabled) {
      setIsListening(false);
      return;
    }

    console.log(`[LiveCalls] Subscribing to audio for call: ${selectedCall}`);
    setIsListening(true);

    const audioChannel = supabase
      .channel(`audio-${selectedCall}`)
      .on(
        "postgres_changes",
        {
          event: "INSERT",
          schema: "public",
          table: "live_call_audio",
          filter: `call_id=eq.${selectedCall}`
        },
        (payload) => {
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
  }, [selectedCall, audioEnabled]);

  // Geocode addresses when they change and verification is enabled
  useEffect(() => {
    if (!addressVerification || !selectedCall) {
      setPickupGeocode(null);
      setDestinationGeocode(null);
      setTripResolveResult(null);
      return;
    }

    const callData = calls.find(c => c.call_id === selectedCall);
    if (!callData) return;

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
          const { data, error } = await supabase.functions.invoke("taxi-trip-resolve", {
            body: {
              pickup_input: callData.pickup || undefined,
              dropoff_input: callData.destination || undefined,
              caller_city_hint: callData.caller_last_pickup ? undefined : "Coventry", // Default hint
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
              lat: data.pickup.lat,
              lon: data.pickup.lng
            });
          } else if (callData.pickup) {
            setPickupGeocode({ found: false, address: callData.pickup, error: "Not found" });
          }

          if (data.dropoff) {
            setDestinationGeocode({
              found: true,
              address: callData.destination!,
              display_name: data.dropoff.formatted_address,
              lat: data.dropoff.lat,
              lon: data.dropoff.lng
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

  const formatTime = (dateStr: string) => {
    const date = new Date(dateStr);
    return date.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
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
            {/* Pipeline Selector */}
            <div className="flex items-center gap-2 bg-card/50 rounded-lg px-3 py-1.5 border border-border">
              <span className={`text-xs font-medium ${!useGeminiPipeline ? 'text-primary' : 'text-muted-foreground'}`}>
                OpenAI
              </span>
              <Switch
                id="pipeline-select"
                checked={useGeminiPipeline}
                onCheckedChange={setUseGeminiPipeline}
              />
              <span className={`text-xs font-medium ${useGeminiPipeline ? 'text-green-400' : 'text-muted-foreground'}`}>
                Gemini
              </span>
            </div>
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
            {/* Audio toggle */}
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
            <Badge variant="outline" className="text-green-400 border-green-400">
              <span className="w-2 h-2 bg-green-400 rounded-full mr-2 animate-pulse" />
              {activeCalls.length} Active
            </Badge>
            <Badge variant="outline" className="text-muted-foreground">
              {calls.length} Total
            </Badge>
          </div>
        </div>

        {/* Pipeline Status Bar */}
        <div className="mb-4 p-3 rounded-lg bg-card/50 border border-border flex items-center justify-between">
          <div className="flex items-center gap-3">
            <div className={`w-2 h-2 rounded-full ${useGeminiPipeline ? 'bg-green-400' : 'bg-primary'}`} />
            <span className="text-sm font-medium">
              Pipeline: {useGeminiPipeline ? 'Gemini (STT → LLM → TTS)' : 'OpenAI Realtime (Audio-to-Audio)'}
            </span>
            <Badge variant="outline" className="text-xs">
              {useGeminiPipeline ? 'FREE LLM' : '~$2.40/M tokens'}
            </Badge>
            <Badge variant="outline" className="text-xs">
              {useGeminiPipeline ? '~800-1200ms latency' : '~400-500ms latency'}
            </Badge>
          </div>
          <code className="text-xs bg-muted px-2 py-1 rounded font-mono text-muted-foreground">
            {useGeminiPipeline 
              ? 'wss://xsdlzoyaosfbbwzmcinq.functions.supabase.co/functions/v1/taxi-realtime-gemini'
              : 'wss://xsdlzoyaosfbbwzmcinq.functions.supabase.co/functions/v1/taxi-realtime'
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
              <Card className="h-[calc(100vh-120px)] flex flex-col">
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
                          <div className="flex items-center gap-2 text-xs text-muted-foreground mb-2">
                            <History className="w-3 h-3" />
                            <span>Last Trip</span>
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
                        <div className="space-y-1">
                          <div className="flex items-center gap-2">
                            <MapPin className="w-4 h-4 text-green-400" />
                            <span className="font-medium">Pickup</span>
                            {addressVerification && pickupGeocode && (
                              pickupGeocode.loading ? (
                                <Loader2 className="w-3 h-3 animate-spin text-muted-foreground" />
                              ) : pickupGeocode.found ? (
                                <CheckCircle2 className="w-4 h-4 text-green-500" />
                              ) : (
                                <XCircle className="w-4 h-4 text-red-500" />
                              )
                            )}
                          </div>
                          <p className="text-muted-foreground pl-6">{selectedCallData.pickup || "—"}</p>
                          {addressVerification && pickupGeocode && !pickupGeocode.loading && (
                            <p className={`text-xs pl-6 ${pickupGeocode.found ? "text-green-400" : "text-red-400"}`}>
                              {pickupGeocode.found 
                                ? `✓ ${pickupGeocode.display_name?.split(",").slice(0, 3).join(",")}` 
                                : `✗ ${pickupGeocode.error || "Not found"}`}
                            </p>
                          )}
                        </div>
                        
                        {/* Destination with verification */}
                        <div className="space-y-1">
                          <div className="flex items-center gap-2">
                            <MapPin className="w-4 h-4 text-red-400" />
                            <span className="font-medium">Destination</span>
                            {addressVerification && destinationGeocode && (
                              destinationGeocode.loading ? (
                                <Loader2 className="w-3 h-3 animate-spin text-muted-foreground" />
                              ) : destinationGeocode.found ? (
                                <CheckCircle2 className="w-4 h-4 text-green-500" />
                              ) : (
                                <XCircle className="w-4 h-4 text-red-500" />
                              )
                            )}
                          </div>
                          <p className="text-muted-foreground pl-6">{selectedCallData.destination || "—"}</p>
                          {addressVerification && destinationGeocode && !destinationGeocode.loading && (
                            <p className={`text-xs pl-6 ${destinationGeocode.found ? "text-green-400" : "text-red-400"}`}>
                              {destinationGeocode.found 
                                ? `✓ ${destinationGeocode.display_name?.split(",").slice(0, 3).join(",")}` 
                                : `✗ ${destinationGeocode.error || "Not found"}`}
                            </p>
                          )}
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
                <div className="flex-1 overflow-y-auto p-4 space-y-3">
                  <h4 className="text-sm font-semibold text-muted-foreground uppercase tracking-wider sticky top-0 bg-card py-2">
                    Live Transcript
                  </h4>
                  
                  {selectedCallData.transcripts.length === 0 ? (
                    <div className="text-center py-12 text-muted-foreground">
                      <Clock className="w-8 h-8 mx-auto mb-2 animate-spin" />
                      <p>Waiting for conversation...</p>
                    </div>
                  ) : (
                    selectedCallData.transcripts.map((t, i) => (
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
                              {formatTime(t.timestamp)}
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
                              {t.role === "user" ? "Customer" : "Ada"} • {formatTime(t.timestamp)}
                            </p>
                          </div>
                        )}
                      </div>
                    ))
                  )}
                </div>
              </Card>
            ) : (
              <Card className="h-[calc(100vh-120px)] flex items-center justify-center">
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
