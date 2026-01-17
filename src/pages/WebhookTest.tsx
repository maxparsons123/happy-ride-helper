import { useState, useRef, useEffect } from "react";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useToast } from "@/hooks/use-toast";
import { supabase } from "@/integrations/supabase/client";
import { TAXI_WEBHOOK_TEST_URL } from "@/config/supabase";
import { 
  ArrowLeft, 
  Phone, 
  Webhook, 
  Send, 
  CheckCircle2, 
  HelpCircle,
  X,
  Clock,
  Mic
} from "lucide-react";
import { Link } from "react-router-dom";

interface WebhookRequest {
  id: string;
  call_id: string;
  caller_phone?: string;
  caller_name?: string;
  timestamp: string;
  transcript: string;
  booking: {
    pickup?: string;
    destination?: string;
    passengers?: number;
    pickup_time?: string;
    intent?: string;
    confirmed?: boolean;
  };
  session_state: Record<string, unknown>;
  conversation: Array<{ role: string; text: string }>;
  status: "pending" | "responded";
  response?: { ada_response?: string; ada_question?: string };
}

interface ConversationMessage {
  role: "user" | "assistant" | "system";
  text: string;
  timestamp: string;
}

export default function WebhookTest() {
  const { toast } = useToast();
  const [requests, setRequests] = useState<WebhookRequest[]>([]);
  const [selectedRequest, setSelectedRequest] = useState<WebhookRequest | null>(null);
  const [responseType, setResponseType] = useState<"response" | "question">("response");
  const [responseText, setResponseText] = useState("");
  const [isConnected, setIsConnected] = useState(false);
  const [conversation, setConversation] = useState<ConversationMessage[]>([]);
  const scrollRef = useRef<HTMLDivElement>(null);

  // Get the test webhook URL from config
  const testWebhookUrl = TAXI_WEBHOOK_TEST_URL;

  // Subscribe to realtime updates from test webhook
  useEffect(() => {
    const channel = supabase
      .channel('webhook-test')
      .on(
        'broadcast',
        { event: 'webhook_request' },
        (payload) => {
          console.log("Received webhook request:", payload);
          const newRequest: WebhookRequest = {
            ...payload.payload,
            id: `req-${Date.now()}`,
            status: "pending",
          };
          setRequests(prev => [newRequest, ...prev]);
          setSelectedRequest(newRequest);
          
          // Add to conversation
          setConversation(prev => [...prev, {
            role: "user",
            text: newRequest.transcript,
            timestamp: newRequest.timestamp,
          }]);
          
          toast({
            title: "New Webhook Request",
            description: `Transcript: "${newRequest.transcript.slice(0, 50)}..."`,
          });
        }
      )
      .subscribe((status) => {
        setIsConnected(status === 'SUBSCRIBED');
        console.log("Realtime subscription status:", status);
      });

    return () => {
      supabase.removeChannel(channel);
    };
  }, [toast]);

  // Auto-scroll conversation
  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [conversation]);

  const handleSendResponse = async () => {
    if (!selectedRequest || !responseText.trim()) return;

    const response = responseType === "response" 
      ? { ada_response: responseText }
      : { ada_question: responseText };

    // Update local state
    setRequests(prev => prev.map(r => 
      r.id === selectedRequest.id 
        ? { ...r, status: "responded" as const, response }
        : r
    ));

    setSelectedRequest({ ...selectedRequest, status: "responded", response });

    // Add to conversation
    setConversation(prev => [...prev, {
      role: "assistant",
      text: responseText,
      timestamp: new Date().toISOString(),
    }]);

    // Send response to the waiting webhook (via broadcast)
    await supabase.channel('webhook-test').send({
      type: 'broadcast',
      event: 'webhook_response',
      payload: {
        call_id: selectedRequest.call_id,
        response,
      },
    });

    toast({
      title: "Response Sent",
      description: responseType === "response" ? "Ada will speak this response" : "Ada will ask this question",
    });

    setResponseText("");
  };

  const handleQuickResponse = (text: string, type: "response" | "question") => {
    setResponseType(type);
    setResponseText(text);
  };

  const simulateRequest = () => {
    const mockRequest: WebhookRequest = {
      id: `req-${Date.now()}`,
      call_id: `test-${Date.now()}`,
      caller_phone: "+447123456789",
      caller_name: "Test Caller",
      timestamp: new Date().toISOString(),
      transcript: "I need a taxi from 52 David Road to Manchester Airport, 2 passengers please",
      booking: {
        pickup: "52 David Road",
        destination: "Manchester Airport",
        passengers: 2,
        pickup_time: "ASAP",
        intent: "booking",
        confirmed: false,
      },
      session_state: { pickup: "52 David Road", destination: "Manchester Airport", passengers: 2 },
      conversation: [{ role: "user", text: "I need a taxi from 52 David Road to Manchester Airport, 2 passengers please" }],
      status: "pending",
    };

    setRequests(prev => [mockRequest, ...prev]);
    setSelectedRequest(mockRequest);
    setConversation(prev => [...prev, {
      role: "user",
      text: mockRequest.transcript,
      timestamp: mockRequest.timestamp,
    }]);
  };

  return (
    <div className="min-h-screen bg-background">
      {/* Header */}
      <header className="border-b bg-card/50 backdrop-blur-sm sticky top-0 z-10">
        <div className="container mx-auto px-4 py-3 flex items-center gap-4">
          <Link to="/live" className="text-muted-foreground hover:text-foreground">
            <ArrowLeft className="h-5 w-5" />
          </Link>
          <div className="flex items-center gap-2">
            <Webhook className="h-5 w-5 text-primary" />
            <h1 className="text-lg font-semibold">Webhook Test Simulator</h1>
          </div>
          <Badge variant={isConnected ? "default" : "secondary"} className="ml-auto">
            {isConnected ? "Connected" : "Disconnected"}
          </Badge>
        </div>
      </header>

      <main className="container mx-auto px-4 py-6">
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Left: Webhook URL & Instructions */}
          <Card className="lg:col-span-1">
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <Webhook className="h-4 w-4" />
                Setup
              </CardTitle>
              <CardDescription>
                Configure your Python bridge to use this webhook URL
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div>
                <label className="text-sm font-medium text-muted-foreground">Test Webhook URL</label>
                <div className="mt-1 p-2 bg-muted rounded text-xs font-mono break-all">
                  {testWebhookUrl}
                </div>
                <Button 
                  variant="outline" 
                  size="sm" 
                  className="mt-2 w-full"
                  onClick={() => {
                    navigator.clipboard.writeText(testWebhookUrl);
                    toast({ title: "Copied to clipboard" });
                  }}
                >
                  Copy URL
                </Button>
              </div>

              <Separator />

              <div>
                <p className="text-sm text-muted-foreground mb-3">
                  When a caller speaks, you'll see the transcript here and can respond in real-time.
                </p>
                <Button 
                  variant="secondary" 
                  size="sm" 
                  className="w-full"
                  onClick={simulateRequest}
                >
                  <Phone className="h-4 w-4 mr-2" />
                  Simulate Incoming Call
                </Button>
              </div>

              <Separator />

              {/* Quick Responses */}
              <div>
                <label className="text-sm font-medium text-muted-foreground">Quick Responses</label>
                <div className="mt-2 space-y-2">
                  <Button 
                    variant="outline" 
                    size="sm" 
                    className="w-full justify-start text-left"
                    onClick={() => handleQuickResponse("Your taxi is booked! Driver Ahmed will arrive in 5 minutes. Fare is approximately £12.50.", "response")}
                  >
                    <CheckCircle2 className="h-4 w-4 mr-2 text-green-500" />
                    Confirm booking
                  </Button>
                  <Button 
                    variant="outline" 
                    size="sm" 
                    className="w-full justify-start text-left"
                    onClick={() => handleQuickResponse("Which David Road do you mean - the one in Coventry or Birmingham?", "question")}
                  >
                    <HelpCircle className="h-4 w-4 mr-2 text-yellow-500" />
                    Ask for clarification
                  </Button>
                  <Button 
                    variant="outline" 
                    size="sm" 
                    className="w-full justify-start text-left"
                    onClick={() => handleQuickResponse("I'm sorry, we don't have any drivers available in that area at the moment.", "response")}
                  >
                    <X className="h-4 w-4 mr-2 text-red-500" />
                    Reject booking
                  </Button>
                  <Button 
                    variant="outline" 
                    size="sm" 
                    className="w-full justify-start text-left"
                    onClick={() => handleQuickResponse("Your taxi will arrive in about 10 minutes. The driver's name is Sarah.", "response")}
                  >
                    <Clock className="h-4 w-4 mr-2 text-blue-500" />
                    Confirm with ETA
                  </Button>
                </div>
              </div>
            </CardContent>
          </Card>

          {/* Middle: Conversation View */}
          <Card className="lg:col-span-1">
            <CardHeader>
              <CardTitle className="text-base flex items-center gap-2">
                <Mic className="h-4 w-4" />
                Live Conversation
              </CardTitle>
              <CardDescription>
                {selectedRequest 
                  ? `Call: ${selectedRequest.call_id}` 
                  : "Waiting for incoming call..."}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <ScrollArea className="h-[400px] pr-4" ref={scrollRef}>
                {conversation.length === 0 ? (
                  <div className="text-center text-muted-foreground py-8">
                    <Phone className="h-8 w-8 mx-auto mb-2 opacity-50" />
                    <p className="text-sm">No messages yet</p>
                    <p className="text-xs mt-1">Click "Simulate Incoming Call" to test</p>
                  </div>
                ) : (
                  <div className="space-y-4">
                    {conversation.map((msg, i) => (
                      <div 
                        key={i}
                        className={`flex ${msg.role === "assistant" ? "justify-end" : "justify-start"}`}
                      >
                        <div 
                          className={`max-w-[85%] rounded-lg px-3 py-2 text-sm ${
                            msg.role === "assistant" 
                              ? "bg-primary text-primary-foreground" 
                              : "bg-muted"
                          }`}
                        >
                          <p>{msg.text}</p>
                          <p className={`text-[10px] mt-1 ${
                            msg.role === "assistant" 
                              ? "text-primary-foreground/70" 
                              : "text-muted-foreground"
                          }`}>
                            {new Date(msg.timestamp).toLocaleTimeString()}
                          </p>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </ScrollArea>

              <Separator className="my-4" />

              {/* Response Input */}
              <div className="space-y-3">
                <Tabs value={responseType} onValueChange={(v) => setResponseType(v as "response" | "question")}>
                  <TabsList className="grid w-full grid-cols-2">
                    <TabsTrigger value="response">
                      <CheckCircle2 className="h-3 w-3 mr-1" />
                      Response
                    </TabsTrigger>
                    <TabsTrigger value="question">
                      <HelpCircle className="h-3 w-3 mr-1" />
                      Question
                    </TabsTrigger>
                  </TabsList>
                </Tabs>

                <Textarea
                  placeholder={responseType === "response" 
                    ? "Type what Ada should say..." 
                    : "Type a question to ask the caller..."}
                  value={responseText}
                  onChange={(e) => setResponseText(e.target.value)}
                  rows={3}
                />

                <Button 
                  className="w-full" 
                  onClick={handleSendResponse}
                  disabled={!responseText.trim() || !selectedRequest}
                >
                  <Send className="h-4 w-4 mr-2" />
                  Send {responseType === "response" ? "Response" : "Question"}
                </Button>
              </div>
            </CardContent>
          </Card>

          {/* Right: Request Details */}
          <Card className="lg:col-span-1">
            <CardHeader>
              <CardTitle className="text-base">Extracted Data</CardTitle>
              <CardDescription>
                Parsed booking information from transcript
              </CardDescription>
            </CardHeader>
            <CardContent>
              {selectedRequest ? (
                <div className="space-y-4">
                  <div className="grid grid-cols-2 gap-3 text-sm">
                    <div>
                      <span className="text-muted-foreground">Call ID</span>
                      <p className="font-mono text-xs">{selectedRequest.call_id}</p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Caller</span>
                      <p>{selectedRequest.caller_phone || "Unknown"}</p>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Status</span>
                      <Badge variant={selectedRequest.status === "pending" ? "secondary" : "default"}>
                        {selectedRequest.status}
                      </Badge>
                    </div>
                    <div>
                      <span className="text-muted-foreground">Intent</span>
                      <Badge variant="outline">{selectedRequest.booking.intent || "unknown"}</Badge>
                    </div>
                  </div>

                  <Separator />

                  <div className="space-y-3">
                    <div>
                      <span className="text-sm text-muted-foreground">Pickup</span>
                      <p className="font-medium">{selectedRequest.booking.pickup || "—"}</p>
                    </div>
                    <div>
                      <span className="text-sm text-muted-foreground">Destination</span>
                      <p className="font-medium">{selectedRequest.booking.destination || "—"}</p>
                    </div>
                    <div className="grid grid-cols-2 gap-3">
                      <div>
                        <span className="text-sm text-muted-foreground">Passengers</span>
                        <p className="font-medium">{selectedRequest.booking.passengers || "—"}</p>
                      </div>
                      <div>
                        <span className="text-sm text-muted-foreground">Time</span>
                        <p className="font-medium">{selectedRequest.booking.pickup_time || "ASAP"}</p>
                      </div>
                    </div>
                  </div>

                  <Separator />

                  <div>
                    <span className="text-sm text-muted-foreground">Raw Transcript</span>
                    <p className="mt-1 text-sm bg-muted p-2 rounded">
                      "{selectedRequest.transcript}"
                    </p>
                  </div>

                  <div>
                    <span className="text-sm text-muted-foreground">Session State</span>
                    <pre className="mt-1 text-xs bg-muted p-2 rounded overflow-auto max-h-24">
                      {JSON.stringify(selectedRequest.session_state, null, 2)}
                    </pre>
                  </div>
                </div>
              ) : (
                <div className="text-center text-muted-foreground py-8">
                  <p className="text-sm">Select a request to view details</p>
                </div>
              )}
            </CardContent>
          </Card>
        </div>

        {/* Request History */}
        <Card className="mt-6">
          <CardHeader>
            <CardTitle className="text-base">Request History</CardTitle>
            <CardDescription>
              All webhook requests received during this session
            </CardDescription>
          </CardHeader>
          <CardContent>
            {requests.length === 0 ? (
              <p className="text-sm text-muted-foreground text-center py-4">
                No requests yet. Simulate a call or connect your Python bridge.
              </p>
            ) : (
              <div className="space-y-2">
                {requests.map(req => (
                  <div 
                    key={req.id}
                    className={`p-3 rounded border cursor-pointer transition-colors ${
                      selectedRequest?.id === req.id 
                        ? "border-primary bg-primary/5" 
                        : "hover:bg-muted"
                    }`}
                    onClick={() => setSelectedRequest(req)}
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        <Phone className="h-4 w-4 text-muted-foreground" />
                        <span className="font-mono text-xs">{req.call_id}</span>
                        <Badge variant={req.status === "pending" ? "secondary" : "default"} className="text-xs">
                          {req.status}
                        </Badge>
                      </div>
                      <span className="text-xs text-muted-foreground">
                        {new Date(req.timestamp).toLocaleTimeString()}
                      </span>
                    </div>
                    <p className="text-sm mt-1 text-muted-foreground truncate">
                      "{req.transcript}"
                    </p>
                  </div>
                ))}
              </div>
            )}
          </CardContent>
        </Card>
      </main>
    </div>
  );
}
