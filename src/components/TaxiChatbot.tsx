import { useState, useRef, useEffect } from "react";
import { Link } from "react-router-dom";
import { supabase } from "@/integrations/supabase/client";
import { ChatMessage } from "./ChatMessage";
import { ChatInput } from "./ChatInput";
import { TypingIndicator } from "./TypingIndicator";
import { BookingStatus } from "./BookingStatus";
import { useToast } from "@/hooks/use-toast";
import { Car, RotateCcw, Mic, Radio } from "lucide-react";
import { Button } from "@/components/ui/button";

interface Message {
  role: "user" | "assistant";
  content: string;
}

interface BookingState {
  pickup: string | null;
  destination: string | null;
  passengers: number | null;
  status: "collecting" | "confirmed" | "info_only";
}

const INITIAL_MESSAGE: Message = {
  role: "assistant",
  content: "Hello! Welcome to Imtech Taxi. Where would you like to be picked up from?",
};

const INITIAL_BOOKING: BookingState = {
  pickup: null,
  destination: null,
  passengers: null,
  status: "collecting",
};

export function TaxiChatbot() {
  const [messages, setMessages] = useState<Message[]>([INITIAL_MESSAGE]);
  const [booking, setBooking] = useState<BookingState>(INITIAL_BOOKING);
  const [isLoading, setIsLoading] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const { toast } = useToast();

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages, isLoading]);

  const handleSend = async (userMessage: string) => {
    const userMsg: Message = { role: "user", content: userMessage };
    setMessages((prev) => [...prev, userMsg]);
    setIsLoading(true);

    try {
      // Build message history for the AI (excluding the initial greeting)
      const messageHistory = [...messages.slice(1), userMsg].map((m) => ({
        role: m.role,
        content: m.content,
      }));

      const { data, error } = await supabase.functions.invoke("taxi-chat", {
        body: {
          messages: messageHistory,
          currentBooking: booking,
        },
      });

      if (error) {
        throw new Error(error.message);
      }

      if (data.error) {
        throw new Error(data.error);
      }

      // Update booking state with extracted info
      setBooking((prev) => ({
        pickup: data.pickup || prev.pickup,
        destination: data.destination || prev.destination,
        passengers: data.passengers ? Number(data.passengers) : prev.passengers,
        status: data.status || prev.status,
      }));

      // Add AI response to messages
      const assistantMsg: Message = {
        role: "assistant",
        content: data.response,
      };
      setMessages((prev) => [...prev, assistantMsg]);

      // Show toast on booking confirmation
      if (data.status === "confirmed") {
        toast({
          title: "Booking Confirmed! 🚕",
          description: "Your taxi is on its way.",
        });
      }
    } catch (error) {
      console.error("Chat error:", error);
      const errorMessage = error instanceof Error ? error.message : "Failed to send message";
      toast({
        title: "Error",
        description: errorMessage,
        variant: "destructive",
      });
      // Add a fallback message
      setMessages((prev) => [
        ...prev,
        {
          role: "assistant",
          content: "I apologize, but I'm having trouble connecting right now. Please try again in a moment.",
        },
      ]);
    } finally {
      setIsLoading(false);
    }
  };

  const handleReset = () => {
    setMessages([INITIAL_MESSAGE]);
    setBooking(INITIAL_BOOKING);
  };

  return (
    <div className="flex h-screen w-full bg-gradient-dark">
      {/* Main Chat Area */}
      <div className="flex flex-1 flex-col">
        {/* Header */}
        <header className="flex items-center justify-between border-b border-chat-border bg-card/80 backdrop-blur-sm px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-gradient-gold shadow-glow">
              <Car className="h-5 w-5 text-primary-foreground" />
            </div>
            <div>
              <h1 className="font-display text-lg font-semibold text-foreground">
                Imtech Taxi
              </h1>
              <p className="text-xs text-muted-foreground">AI Dispatcher</p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="sm"
              asChild
              className="text-muted-foreground hover:text-foreground"
            >
              <Link to="/live">
                <Radio className="mr-2 h-4 w-4" />
                Live Calls
              </Link>
            </Button>
            <Button
              variant="ghost"
              size="sm"
              asChild
              className="text-muted-foreground hover:text-foreground"
            >
              <Link to="/voice-test">
                <Mic className="mr-2 h-4 w-4" />
                Voice Test
              </Link>
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={handleReset}
              className="text-muted-foreground hover:text-foreground"
            >
              <RotateCcw className="mr-2 h-4 w-4" />
              New Booking
            </Button>
          </div>
        </header>

        {/* Messages */}
        <div className="flex-1 overflow-y-auto px-6 py-4 space-y-4">
          {messages.map((msg, idx) => (
            <ChatMessage key={idx} role={msg.role} content={msg.content} />
          ))}
          {isLoading && <TypingIndicator />}
          <div ref={messagesEndRef} />
        </div>

        {/* Input */}
        {booking.status === "confirmed" ? (
          <div className="border-t border-chat-border bg-card/80 backdrop-blur-sm px-6 py-4">
            <div className="flex items-center justify-center gap-4">
              <p className="text-muted-foreground">Booking complete!</p>
              <Button onClick={handleReset} className="bg-gradient-gold hover:opacity-90">
                Book Another Taxi
              </Button>
            </div>
          </div>
        ) : (
          <ChatInput onSend={handleSend} disabled={isLoading} />
        )}
      </div>

      {/* Sidebar - Booking Status */}
      <aside className="hidden w-80 border-l border-chat-border bg-card/50 backdrop-blur-sm p-6 lg:block">
        <BookingStatus
          pickup={booking.pickup}
          destination={booking.destination}
          passengers={booking.passengers}
          status={booking.status}
        />

        {/* Quick Info */}
        <div className="mt-6 space-y-3">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            Quick Info
          </h4>
          <div className="space-y-2 text-sm">
            <div className="flex justify-between">
              <span className="text-muted-foreground">Wait time</span>
              <span className="text-foreground">5-8 min</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">City trips</span>
              <span className="text-foreground">£15-£25</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Airport</span>
              <span className="text-foreground">£45</span>
            </div>
            <div className="flex justify-between">
              <span className="text-muted-foreground">Availability</span>
              <span className="text-primary font-medium">24/7</span>
            </div>
          </div>
        </div>
      </aside>
    </div>
  );
}
