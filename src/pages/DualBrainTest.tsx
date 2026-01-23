import { useState, useRef } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { Brain, MessageSquare, Send, RotateCcw, Zap, Clock, CheckCircle2, XCircle } from "lucide-react";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";

// Server state interface (must match edge function)
interface ServerState {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  pickup_time: string | null;
  lastQuestion: string;
  step: "collecting" | "summary" | "confirmed";
  conversationHistory: Array<{ role: string; content: string }>;
}

interface Turn {
  id: number;
  userInput: string;
  adaResponse: string;
  extraction: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    pickup_time: string | null;
    is_affirmative: boolean;
    is_correction: boolean;
  };
  state: {
    pickup: string | null;
    destination: string | null;
    passengers: number | null;
    pickup_time: string | null;
    step: string;
  };
  processingTime: number;
  timestamp: Date;
}

export default function DualBrainTest() {
  const [input, setInput] = useState("");
  const [turns, setTurns] = useState<Turn[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [currentState, setCurrentState] = useState<Turn["state"] | null>(null);
  // Server state for stateless edge function (passed with each request)
  const [serverState, setServerState] = useState<ServerState | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const sendMessage = async () => {
    if (!input.trim() || isLoading) return;

    const userInput = input.trim();
    setInput("");
    setIsLoading(true);

    try {
      const startTime = Date.now();
      const { data, error } = await supabase.functions.invoke("taxi-dual-brain-test", {
        body: { 
          transcript: userInput,
          state: serverState // Pass current state to edge function
        },
      });

      if (error) throw error;

      const turn: Turn = {
        id: turns.length + 1,
        userInput,
        adaResponse: data.speech,
        extraction: data.extraction || {},
        state: data.state,
        processingTime: data.processingTime || (Date.now() - startTime),
        timestamp: new Date(),
      };

      setTurns((prev) => [...prev, turn]);
      setCurrentState(data.state);
      // Store updated state for next request
      setServerState(data.state);

      if (data.end) {
        toast.success("Booking confirmed!");
      }
    } catch (err) {
      console.error("Error:", err);
      toast.error("Failed to process message");
    } finally {
      setIsLoading(false);
      inputRef.current?.focus();
    }
  };

  const resetSession = () => {
    setTurns([]);
    setCurrentState(null);
    setServerState(null);
    toast.success("Session reset");
  };

  const getStepBadge = (step: string) => {
    switch (step) {
      case "collecting":
        return <Badge variant="secondary">Collecting</Badge>;
      case "summary":
        return <Badge className="bg-amber-500">Summary</Badge>;
      case "confirmed":
        return <Badge className="bg-green-500">Confirmed</Badge>;
      default:
        return <Badge variant="outline">{step}</Badge>;
    }
  };

  return (
    <div className="min-h-screen bg-background p-4">
      <div className="max-w-6xl mx-auto space-y-4">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <Brain className="h-8 w-8 text-primary" />
            <div>
              <h1 className="text-2xl font-bold">Dual-Brain Architecture Test</h1>
              <p className="text-muted-foreground text-sm">
                Brain 1: Intent Extraction → Brain 2: Speech Generation
              </p>
            </div>
          </div>
          <Button variant="outline" onClick={resetSession}>
            <RotateCcw className="h-4 w-4 mr-2" />
            Reset Session
          </Button>
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
          {/* Chat Panel */}
          <Card className="lg:col-span-2">
            <CardHeader className="pb-3">
              <CardTitle className="flex items-center gap-2">
                <MessageSquare className="h-5 w-5" />
                Conversation
              </CardTitle>
              <CardDescription>
                Stateless dual-brain architecture test
              </CardDescription>
            </CardHeader>
            <CardContent>
              <ScrollArea className="h-[500px] pr-4">
                {/* Ada's intro message - always shown */}
                <div className="space-y-4">
                  <div className="flex justify-start">
                    <div className="bg-muted rounded-lg px-4 py-2 max-w-[80%]">
                      <p>Hello, and welcome to the Taxibot demo. I'm Ada, your taxi booking assistant. Where would you like to be picked up?</p>
                      <div className="flex items-center gap-2 mt-2 text-xs text-muted-foreground">
                        <Badge variant="secondary">Collecting</Badge>
                      </div>
                    </div>
                  </div>
                  
                  {turns.length === 0 && (
                    <div className="text-center text-muted-foreground py-8">
                      <p className="text-sm">Type your pickup address to continue...</p>
                    </div>
                  )}
                  
                  {turns.map((turn) => (
                    <div key={turn.id} className="space-y-2">
                      {/* User message */}
                      <div className="flex justify-end">
                        <div className="bg-primary text-primary-foreground rounded-lg px-4 py-2 max-w-[80%]">
                          {turn.userInput}
                        </div>
                      </div>
                      
                      {/* Ada response */}
                      <div className="flex justify-start">
                        <div className="bg-muted rounded-lg px-4 py-2 max-w-[80%]">
                          <p>{turn.adaResponse}</p>
                          <div className="flex items-center gap-2 mt-2 text-xs text-muted-foreground">
                            <Clock className="h-3 w-3" />
                            {turn.processingTime}ms
                            {getStepBadge(turn.state.step)}
                          </div>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              </ScrollArea>

              {/* Input */}
              <div className="flex gap-2 mt-4">
                <Input
                  ref={inputRef}
                  value={input}
                  onChange={(e) => setInput(e.target.value)}
                  onKeyDown={(e) => e.key === "Enter" && sendMessage()}
                  placeholder="Type your message..."
                  disabled={isLoading}
                />
                <Button onClick={sendMessage} disabled={isLoading || !input.trim()}>
                  <Send className="h-4 w-4" />
                </Button>
              </div>
            </CardContent>
          </Card>

          {/* State Panel */}
          <div className="space-y-4">
            {/* Current State */}
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="flex items-center gap-2 text-base">
                  <Zap className="h-4 w-4" />
                  Current State
                </CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <div className="flex items-center justify-between">
                  <span className="text-sm text-muted-foreground">Pickup</span>
                  <div className="flex items-center gap-2">
                    {currentState?.pickup ? (
                      <>
                        <CheckCircle2 className="h-4 w-4 text-green-500" />
                        <span className="text-sm font-medium">{currentState.pickup}</span>
                      </>
                    ) : (
                      <>
                        <XCircle className="h-4 w-4 text-muted-foreground" />
                        <span className="text-sm text-muted-foreground">Not set</span>
                      </>
                    )}
                  </div>
                </div>
                <Separator />
                <div className="flex items-center justify-between">
                  <span className="text-sm text-muted-foreground">Destination</span>
                  <div className="flex items-center gap-2">
                    {currentState?.destination ? (
                      <>
                        <CheckCircle2 className="h-4 w-4 text-green-500" />
                        <span className="text-sm font-medium">{currentState.destination}</span>
                      </>
                    ) : (
                      <>
                        <XCircle className="h-4 w-4 text-muted-foreground" />
                        <span className="text-sm text-muted-foreground">Not set</span>
                      </>
                    )}
                  </div>
                </div>
                <Separator />
                <div className="flex items-center justify-between">
                  <span className="text-sm text-muted-foreground">Passengers</span>
                  <div className="flex items-center gap-2">
                    {currentState?.passengers ? (
                      <>
                        <CheckCircle2 className="h-4 w-4 text-green-500" />
                        <span className="text-sm font-medium">{currentState.passengers}</span>
                      </>
                    ) : (
                      <>
                        <XCircle className="h-4 w-4 text-muted-foreground" />
                        <span className="text-sm text-muted-foreground">Not set</span>
                      </>
                    )}
                  </div>
                </div>
                <Separator />
                <div className="flex items-center justify-between">
                  <span className="text-sm text-muted-foreground">Pickup Time</span>
                  <div className="flex items-center gap-2">
                    {currentState?.pickup_time ? (
                      <>
                        <CheckCircle2 className="h-4 w-4 text-green-500" />
                        <span className="text-sm font-medium">{currentState.pickup_time}</span>
                      </>
                    ) : (
                      <>
                        <XCircle className="h-4 w-4 text-muted-foreground" />
                        <span className="text-sm text-muted-foreground">Not set</span>
                      </>
                    )}
                  </div>
                </div>
                <Separator />
                <div className="flex items-center justify-between">
                  <span className="text-sm text-muted-foreground">Step</span>
                  {currentState ? getStepBadge(currentState.step) : <Badge variant="outline">-</Badge>}
                </div>
              </CardContent>
            </Card>

            {/* Latest Extraction */}
            {turns.length > 0 && (
              <Card>
                <CardHeader className="pb-3">
                  <CardTitle className="flex items-center gap-2 text-base">
                    <Brain className="h-4 w-4" />
                    Brain 1: Last Extraction
                  </CardTitle>
                </CardHeader>
                <CardContent>
                  <pre className="text-xs bg-muted p-3 rounded-lg overflow-auto">
                    {JSON.stringify(turns[turns.length - 1].extraction, null, 2)}
                  </pre>
                </CardContent>
              </Card>
            )}

            {/* Quick Test Phrases */}
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base">Quick Tests</CardTitle>
              </CardHeader>
              <CardContent className="space-y-2">
                {[
                  "52A David Road",
                  "7 Russell Street",
                  "Three passengers",
                  "As soon as possible",
                  "Yes, that's correct",
                ].map((phrase) => (
                  <Button
                    key={phrase}
                    variant="outline"
                    size="sm"
                    className="w-full justify-start"
                    onClick={() => {
                      setInput(phrase);
                      inputRef.current?.focus();
                    }}
                  >
                    {phrase}
                  </Button>
                ))}
              </CardContent>
            </Card>
          </div>
        </div>
      </div>
    </div>
  );
}
