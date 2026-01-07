import { useEffect, useState } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Card } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Phone, PhoneOff, MapPin, Users, Clock, DollarSign, Radio } from "lucide-react";

interface Transcript {
  role: string;
  text: string;
  timestamp: string;
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
}

export default function LiveCalls() {
  const [calls, setCalls] = useState<LiveCall[]>([]);
  const [selectedCall, setSelectedCall] = useState<string | null>(null);

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
            <Radio className="w-8 h-8 text-primary animate-pulse" />
            <h1 className="text-3xl font-display font-bold text-primary">Live Asterisk Streams</h1>
          </div>
          <div className="flex items-center gap-4">
            <Badge variant="outline" className="text-green-400 border-green-400">
              <span className="w-2 h-2 bg-green-400 rounded-full mr-2 animate-pulse" />
              {activeCalls.length} Active
            </Badge>
            <Badge variant="outline" className="text-muted-foreground">
              {calls.length} Total
            </Badge>
          </div>
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
              <Card className="h-[calc(100vh-180px)] flex flex-col">
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

                  {/* Booking Info */}
                  {selectedCallData.booking_confirmed && (
                    <div className="mt-4 p-3 bg-primary/10 rounded-lg border border-primary/30">
                      <p className="text-sm font-semibold text-primary mb-2">✅ Booking Confirmed</p>
                      <div className="grid grid-cols-4 gap-4 text-sm">
                        <div className="flex items-center gap-2">
                          <MapPin className="w-4 h-4 text-green-400" />
                          <span>{selectedCallData.pickup || "—"}</span>
                        </div>
                        <div className="flex items-center gap-2">
                          <MapPin className="w-4 h-4 text-red-400" />
                          <span>{selectedCallData.destination || "—"}</span>
                        </div>
                        <div className="flex items-center gap-2">
                          <Users className="w-4 h-4" />
                          <span>{selectedCallData.passengers || "—"} passengers</span>
                        </div>
                        <div className="flex items-center gap-2">
                          <DollarSign className="w-4 h-4 text-primary" />
                          <span>{selectedCallData.fare || "—"}</span>
                        </div>
                      </div>
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
                        className={`flex ${t.role === "user" ? "justify-end" : "justify-start"}`}
                      >
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
                      </div>
                    ))
                  )}
                </div>
              </Card>
            ) : (
              <Card className="h-[calc(100vh-180px)] flex items-center justify-center">
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
