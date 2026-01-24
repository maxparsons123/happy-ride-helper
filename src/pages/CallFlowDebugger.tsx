import { useState, useEffect, useRef } from "react";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ScrollArea } from "@/components/ui/scroll-area";
import { supabase } from "@/integrations/supabase/client";
import { ArrowLeft, Phone, Mic, Bot, CheckCircle2, XCircle, RefreshCw, Search, Radio, Wifi } from "lucide-react";
import { Link } from "react-router-dom";

interface QAPair {
  id: string;
  questionType: string;
  adaQuestion: string;
  userResponse: string | null;
  extractedValue: string | null;
  accepted: boolean;
  timestamp: Date;
}

interface CallFlow {
  callId: string;
  phone: string;
  startedAt: Date;
  status: string;
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  qaPairs: QAPair[];
}

export default function CallFlowDebugger() {
  const [calls, setCalls] = useState<CallFlow[]>([]);
  const [selectedCall, setSelectedCall] = useState<CallFlow | null>(null);
  const [searchTerm, setSearchTerm] = useState("");
  const [loading, setLoading] = useState(true);
  const [isLive, setIsLive] = useState(false);
  const [lastUpdate, setLastUpdate] = useState<Date | null>(null);
  const [flashCallId, setFlashCallId] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  const fetchCalls = async () => {
    setLoading(true);
    const { data, error } = await supabase
      .from("live_calls")
      .select("*")
      .order("started_at", { ascending: false })
      .limit(20);

    if (error) {
      console.error("Error fetching calls:", error);
      setLoading(false);
      return;
    }

    const flowData: CallFlow[] = (data || []).map((call) => {
      const transcripts = (call.transcripts as Array<{ role: string; content: string; timestamp?: string }>) || [];
      
      // Parse transcripts into Q&A pairs
      const qaPairs: QAPair[] = [];
      let currentQuestion: { type: string; text: string; timestamp: Date } | null = null;
      
      transcripts.forEach((t, idx) => {
        if (t.role === "assistant") {
          // Detect question type from content
          let questionType = "general";
          const lower = t.content.toLowerCase();
          if (lower.includes("pick") && lower.includes("up")) questionType = "pickup";
          else if (lower.includes("destination") || lower.includes("going") || lower.includes("where to")) questionType = "destination";
          else if (lower.includes("how many") || lower.includes("passenger") || lower.includes("people")) questionType = "passengers";
          else if (lower.includes("when") || lower.includes("time") || lower.includes("now")) questionType = "time";
          else if (lower.includes("confirm") || lower.includes("book that") || lower.includes("shall i")) questionType = "confirmation";
          
          currentQuestion = {
            type: questionType,
            text: t.content,
            timestamp: t.timestamp ? new Date(t.timestamp) : new Date(call.started_at),
          };
        } else if (t.role === "user" && currentQuestion) {
          qaPairs.push({
            id: `${call.call_id}-${idx}`,
            questionType: currentQuestion.type,
            adaQuestion: currentQuestion.text,
            userResponse: t.content,
            extractedValue: getExtractedValue(currentQuestion.type, call),
            accepted: true,
            timestamp: currentQuestion.timestamp,
          });
          currentQuestion = null;
        }
      });

      return {
        callId: call.call_id,
        phone: call.caller_phone || "Unknown",
        startedAt: new Date(call.started_at),
        status: call.status,
        pickup: call.pickup,
        destination: call.destination,
        passengers: call.passengers,
        qaPairs,
      };
    });

    setCalls(flowData);
    setLoading(false);
    setLastUpdate(new Date());
  };

  const getExtractedValue = (questionType: string, call: any): string | null => {
    switch (questionType) {
      case "pickup": return call.pickup;
      case "destination": return call.destination;
      case "passengers": return call.passengers?.toString();
      case "time": return call.pickup_time;
      case "confirmation": return call.booking_confirmed ? "Yes" : "Pending";
      default: return null;
    }
  };

  useEffect(() => {
    fetchCalls();

    // Subscribe to live updates with enhanced handling
    const channel = supabase
      .channel("call-flow-updates")
      .on(
        "postgres_changes",
        { event: "*", schema: "public", table: "live_calls" },
        (payload) => {
          console.log("[CallFlow] 🔄 Realtime update:", payload.eventType, payload.new);
          setLastUpdate(new Date());
          
          // Flash the updated call
          const updatedCallId = (payload.new as any)?.call_id;
          if (updatedCallId) {
            setFlashCallId(updatedCallId);
            setTimeout(() => setFlashCallId(null), 1500);
          }
          
          // Refetch to update the list
          fetchCalls().then(() => {
            // Auto-update selected call if it's the one that changed
            if (selectedCall && updatedCallId === selectedCall.callId) {
              setCalls(prev => {
                const updated = prev.find(c => c.callId === updatedCallId);
                if (updated) setSelectedCall(updated);
                return prev;
              });
            }
          });
        }
      )
      .subscribe((status) => {
        console.log("[CallFlow] 📡 Subscription status:", status);
        setIsLive(status === "SUBSCRIBED");
      });

    return () => {
      supabase.removeChannel(channel);
    };
  }, []);

  const filteredCalls = calls.filter(
    (c) =>
      c.callId.toLowerCase().includes(searchTerm.toLowerCase()) ||
      c.phone.includes(searchTerm)
  );

  const getQuestionBadgeColor = (type: string) => {
    switch (type) {
      case "pickup": return "bg-blue-500/20 text-blue-400 border-blue-500/30";
      case "destination": return "bg-purple-500/20 text-purple-400 border-purple-500/30";
      case "passengers": return "bg-green-500/20 text-green-400 border-green-500/30";
      case "time": return "bg-orange-500/20 text-orange-400 border-orange-500/30";
      case "confirmation": return "bg-yellow-500/20 text-yellow-400 border-yellow-500/30";
      default: return "bg-muted text-muted-foreground";
    }
  };

  return (
    <div className="min-h-screen bg-background p-4 md:p-6">
      <div className="max-w-7xl mx-auto space-y-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-4">
            <Link to="/">
              <Button variant="ghost" size="icon">
                <ArrowLeft className="h-5 w-5" />
              </Button>
            </Link>
            <div>
              <h1 className="text-2xl font-bold">Call Flow Debugger</h1>
              <p className="text-muted-foreground text-sm">
                Visualize Q&A exchanges between Ada and callers
              </p>
            </div>
          </div>
          <div className="flex items-center gap-3">
            {/* Live indicator */}
            <div className={`flex items-center gap-2 px-3 py-1.5 rounded-full text-xs font-medium ${
              isLive 
                ? "bg-green-500/20 text-green-400 border border-green-500/30" 
                : "bg-yellow-500/20 text-yellow-400 border border-yellow-500/30"
            }`}>
              {isLive ? (
                <>
                  <Wifi className="h-3 w-3 animate-pulse" />
                  Live
                </>
              ) : (
                <>
                  <Radio className="h-3 w-3" />
                  Connecting...
                </>
              )}
            </div>
            {lastUpdate && (
              <span className="text-xs text-muted-foreground">
                Updated {lastUpdate.toLocaleTimeString()}
              </span>
            )}
            <Button onClick={fetchCalls} variant="outline" size="sm">
              <RefreshCw className={`h-4 w-4 mr-2 ${loading ? "animate-spin" : ""}`} />
              Refresh
            </Button>
          </div>
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Call List */}
          <Card className="lg:col-span-1">
            <CardHeader className="pb-3">
              <CardTitle className="text-lg flex items-center gap-2">
                <Phone className="h-4 w-4" />
                Recent Calls
                {calls.filter(c => c.status === "active").length > 0 && (
                  <Badge className="bg-green-500/20 text-green-400 text-xs animate-pulse">
                    {calls.filter(c => c.status === "active").length} active
                  </Badge>
                )}
              </CardTitle>
              <div className="relative">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
                <Input
                  placeholder="Search by call ID or phone..."
                  value={searchTerm}
                  onChange={(e) => setSearchTerm(e.target.value)}
                  className="pl-9"
                />
              </div>
            </CardHeader>
            <CardContent className="p-0">
              <ScrollArea className="h-[600px]">
                <div className="space-y-1 p-2">
                  {filteredCalls.map((call) => (
                    <button
                      key={call.callId}
                      onClick={() => setSelectedCall(call)}
                      className={`w-full text-left p-3 rounded-lg transition-all duration-300 ${
                        selectedCall?.callId === call.callId
                          ? "bg-primary/10 border border-primary/30"
                          : "hover:bg-muted/50"
                      } ${
                        flashCallId === call.callId
                          ? "ring-2 ring-green-500 bg-green-500/10"
                          : ""
                      }`}
                    >
                      <div className="flex items-center justify-between mb-1">
                        <span className="font-mono text-xs text-muted-foreground">
                          {call.callId.slice(0, 12)}...
                        </span>
                        <Badge
                          variant="outline"
                          className={
                            call.status === "active"
                              ? "bg-green-500/20 text-green-400"
                              : "bg-muted"
                          }
                        >
                          {call.status}
                        </Badge>
                      </div>
                      <div className="text-sm font-medium">{call.phone}</div>
                      <div className="text-xs text-muted-foreground mt-1">
                        {call.qaPairs.length} exchanges •{" "}
                        {call.startedAt.toLocaleTimeString()}
                      </div>
                    </button>
                  ))}
                  {filteredCalls.length === 0 && (
                    <div className="text-center text-muted-foreground py-8">
                      No calls found
                    </div>
                  )}
                </div>
              </ScrollArea>
            </CardContent>
          </Card>

          {/* Flow Visualization */}
          <Card className="lg:col-span-2">
            <CardHeader className="pb-3">
              <CardTitle className="text-lg">
                {selectedCall ? (
                  <div className="flex items-center justify-between">
                    <span>Call Flow: {selectedCall.phone}</span>
                    <div className="flex gap-2">
                      {selectedCall.pickup && (
                        <Badge variant="outline" className="text-xs">
                          📍 {selectedCall.pickup}
                        </Badge>
                      )}
                      {selectedCall.destination && (
                        <Badge variant="outline" className="text-xs">
                          🎯 {selectedCall.destination}
                        </Badge>
                      )}
                      {selectedCall.passengers && (
                        <Badge variant="outline" className="text-xs">
                          👥 {selectedCall.passengers}
                        </Badge>
                      )}
                    </div>
                  </div>
                ) : (
                  "Select a call to view flow"
                )}
              </CardTitle>
            </CardHeader>
            <CardContent>
              {selectedCall ? (
                <ScrollArea className="h-[550px] pr-4">
                  <div className="space-y-6">
                    {selectedCall.qaPairs.length === 0 ? (
                      <div className="text-center text-muted-foreground py-12">
                        No Q&A exchanges recorded yet
                      </div>
                    ) : (
                      selectedCall.qaPairs.map((qa, idx) => (
                        <div key={qa.id} className="relative">
                          {/* Connection line */}
                          {idx < selectedCall.qaPairs.length - 1 && (
                            <div className="absolute left-6 top-full h-6 w-0.5 bg-border" />
                          )}

                          {/* Q&A Block */}
                          <div className="space-y-3">
                            {/* Ada's Question */}
                            <div className="flex gap-3">
                              <div className="flex-shrink-0 w-12 h-12 rounded-full bg-primary/20 flex items-center justify-center">
                                <Bot className="h-6 w-6 text-primary" />
                              </div>
                              <div className="flex-1 space-y-2">
                                <div className="flex items-center gap-2">
                                  <span className="font-semibold text-sm">Ada</span>
                                  <Badge
                                    variant="outline"
                                    className={`text-xs ${getQuestionBadgeColor(qa.questionType)}`}
                                  >
                                    {qa.questionType.toUpperCase()}
                                  </Badge>
                                  <span className="text-xs text-muted-foreground">
                                    {qa.timestamp.toLocaleTimeString()}
                                  </span>
                                </div>
                                <div className="bg-muted/50 rounded-lg p-3 text-sm">
                                  "{qa.adaQuestion}"
                                </div>
                              </div>
                            </div>

                            {/* User's Response */}
                            {qa.userResponse && (
                              <div className="flex gap-3 ml-8">
                                <div className="flex-shrink-0 w-10 h-10 rounded-full bg-secondary/50 flex items-center justify-center">
                                  <Mic className="h-5 w-5 text-secondary-foreground" />
                                </div>
                                <div className="flex-1 space-y-2">
                                  <div className="flex items-center gap-2">
                                    <span className="font-semibold text-sm">Caller</span>
                                  </div>
                                  <div className="bg-secondary/30 rounded-lg p-3 text-sm border border-secondary/50">
                                    "{qa.userResponse}"
                                  </div>
                                </div>
                              </div>
                            )}

                            {/* Extraction Result */}
                            {qa.extractedValue && (
                              <div className="ml-16 flex items-center gap-2 text-xs">
                                {qa.accepted ? (
                                  <CheckCircle2 className="h-4 w-4 text-green-500" />
                                ) : (
                                  <XCircle className="h-4 w-4 text-red-500" />
                                )}
                                <span className="text-muted-foreground">Extracted:</span>
                                <Badge variant="secondary" className="font-mono">
                                  {qa.extractedValue}
                                </Badge>
                              </div>
                            )}
                          </div>
                        </div>
                      ))
                    )}
                  </div>
                </ScrollArea>
              ) : (
                <div className="h-[550px] flex items-center justify-center text-muted-foreground">
                  <div className="text-center space-y-2">
                    <Phone className="h-12 w-12 mx-auto opacity-30" />
                    <p>Select a call from the list to view the Q&A flow</p>
                  </div>
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
