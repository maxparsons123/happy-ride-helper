import { serve } from "https://deno.land/std@0.168.0/http/server.ts";
import { createClient } from "https://esm.sh/@supabase/supabase-js@2.89.0";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
};

/**
 * taxi-dispatch-callback
 * 
 * Webhook endpoint for external dispatch systems to send messages back to Ada.
 * 
 * ACTIONS (use "action" field):
 * 
 * 1. DISPATCH CONFIRMATION - Tell Ada to confirm the booking:
 * {
 *   "call_id": "abc123",
 *   "action": "confirm",
 *   "status": "dispatched",
 *   "eta": "5 minutes",
 *   "driver_name": "John",
 *   "vehicle_reg": "AB12 CDE",
 *   "fare": "£15.00"
 * }
 * 
 * 2. ASK QUESTION - Make Ada ask the customer something:
 * {
 *   "call_id": "abc123",
 *   "action": "ask",
 *   "question": "The driver wants to know - front entrance or back entrance?",
 *   "context": "entrance_choice"  // optional - helps track the response
 * }
 * 
 * 3. SAY MESSAGE - Make Ada say something (no response expected):
 * {
 *   "call_id": "abc123",
 *   "action": "say",
 *   "message": "Just to let you know, your driver John is running 2 minutes late."
 * }
 * 
 * 4. REJECT/NO CARS:
 * {
 *   "call_id": "abc123",
 *   "action": "confirm",
 *   "status": "rejected" | "no_cars",
 *   "message": "Sorry, we can't service that area."
 * }
 * 
 * CALL ACTIONS (use "call_action" field):
 * 
 * 5. HANGUP - Instruct Ada to end the call:
 * {
 *   "call_id": "abc123",
 *   "call_action": "hangup",
 *   "message": "Goodbye!"  // optional - Ada will say this before hanging up
 * }
 */

interface DispatchCallback {
  call_id: string;
  action?: "confirm" | "ask" | "say" | "booked"; // "booked" is alias for "confirm"
  call_action?: "hangup";
  // For confirm action
  status?: "dispatched" | "rejected" | "no_cars" | "pending";
  eta?: string;
  eta_minutes?: number; // Numeric ETA alternative
  driver_name?: string;
  vehicle_reg?: string;
  vehicle_type?: string;
  fare?: string;
  message?: string;
  booking_ref?: string; // Reference number for the booking
  // For ask action
  question?: string;
  context?: string;
  // For say action (uses message field)
  // For hangup call_action - optional goodbye message
}

serve(async (req) => {
  if (req.method === "OPTIONS") {
    return new Response(null, { headers: corsHeaders });
  }

  try {
    const SUPABASE_URL = Deno.env.get("SUPABASE_URL");
    const SUPABASE_SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");

    if (!SUPABASE_URL || !SUPABASE_SERVICE_ROLE_KEY) {
      throw new Error("Missing Supabase configuration");
    }

    const callback: DispatchCallback = await req.json();
    const { 
      call_id, 
      action,
      call_action,
      status, 
      eta,
      eta_minutes,
      driver_name, 
      vehicle_reg, 
      vehicle_type, 
      fare, 
      message,
      booking_ref,
      question,
      context 
    } = callback;
    
    // Normalize ETA: prefer eta_minutes if provided
    const normalizedEta = eta_minutes ? `${eta_minutes} minutes` : eta;

    console.log(`[${call_id}] 📥 Dispatch callback: action=${action}, call_action=${call_action}`);
    console.log(`[${call_id}] Payload:`, JSON.stringify(callback));

    if (!call_id) {
      return new Response(JSON.stringify({ 
        error: "Missing required field: call_id" 
      }), {
        status: 400,
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    const supabase = createClient(SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY);

    // ═══════════════════════════════════════════════════════════════════
    // CALL_ACTION: HANGUP - Instruct Ada to end the call (check first)
    // ═══════════════════════════════════════════════════════════════════
    if (call_action === "hangup") {
      const goodbyeMessage = message || "Goodbye!";
      console.log(`[${call_id}] 📞 Dispatch hangup: "${goodbyeMessage}"`);

      // Add hangup instruction to transcripts so polling can detect it
      const { data: callData } = await supabase
        .from("live_calls")
        .select("transcripts")
        .eq("call_id", call_id)
        .single();

      const transcripts = (callData?.transcripts as any[]) || [];
      transcripts.push({
        role: "dispatch_hangup",
        text: goodbyeMessage,
        timestamp: new Date().toISOString()
      });

      await supabase
        .from("live_calls")
        .update({
          transcripts,
          updated_at: new Date().toISOString()
        })
        .eq("call_id", call_id);

      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: "dispatch_hangup",
        payload: {
          call_id,
          call_action: "hangup",
          message: goodbyeMessage,
          timestamp: new Date().toISOString()
        }
      });

      return new Response(JSON.stringify({
        success: true,
        call_id,
        call_action: "hangup",
        message: "Hangup instruction sent to Ada"
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION: ASK - Send a question for Ada to ask the customer
    // ═══════════════════════════════════════════════════════════════════
    if (action === "ask") {
      if (!question) {
        return new Response(JSON.stringify({ 
          error: "Missing required field for 'ask' action: question" 
        }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      console.log(`[${call_id}] 🎤 Dispatch asking: "${question}"`);

      // Broadcast the question to the active WebSocket session
      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: "dispatch_ask",
        payload: {
          call_id,
          action: "ask",
          question,
          context: context || null,
          timestamp: new Date().toISOString()
        }
      });

      // Also update live_calls to show pending question
      await supabase
        .from("live_calls")
        .update({
          clarification_attempts: { 
            pending_question: question,
            question_context: context || null,
            asked_at: new Date().toISOString()
          },
          updated_at: new Date().toISOString()
        })
        .eq("call_id", call_id);

      return new Response(JSON.stringify({
        success: true,
        call_id,
        action: "ask",
        question,
        message: "Question sent to Ada"
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION: SAY - Make Ada say something (no response expected)
    // ═══════════════════════════════════════════════════════════════════
    if (action === "say") {
      if (!message) {
        return new Response(JSON.stringify({ 
          error: "Missing required field for 'say' action: message" 
        }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      console.log(`[${call_id}] 📢 Dispatch saying: "${message}"`);

      // Add dispatch message to transcripts so polling can detect it
      const { data: callData } = await supabase
        .from("live_calls")
        .select("transcripts")
        .eq("call_id", call_id)
        .single();

      const transcripts = (callData?.transcripts as any[]) || [];
      transcripts.push({
        role: "dispatch",
        text: message,
        timestamp: new Date().toISOString()
      });

      await supabase
        .from("live_calls")
        .update({
          transcripts,
          updated_at: new Date().toISOString()
        })
        .eq("call_id", call_id);

      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: "dispatch_say",
        payload: {
          call_id,
          action: "say",
          message,
          timestamp: new Date().toISOString()
        }
      });

      return new Response(JSON.stringify({
        success: true,
        call_id,
        action: "say",
        message: "Message sent to Ada"
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION: CONFIRM/BOOKED - Standard booking confirmation/rejection
    // "booked" is an alias for "confirm" for backwards compatibility
    // ═══════════════════════════════════════════════════════════════════
    if (!action || action === "confirm" || action === "booked") {
      if (!status) {
        return new Response(JSON.stringify({ 
          error: "Missing required field for 'confirm' action: status" 
        }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      // Build confirmation message based on status
      let confirmationMessage: string;
      let bookingStatus: string;

      if (status === "dispatched") {
        bookingStatus = "dispatched";
        const parts: string[] = ["Brilliant! Your taxi is confirmed."];
        
        if (driver_name) {
          parts.push(`Your driver ${driver_name} is on the way.`);
        }
        if (normalizedEta) {
          parts.push(`They'll be with you in ${normalizedEta}.`);
        }
        if (vehicle_reg) {
          parts.push(`Look out for registration ${vehicle_reg}.`);
        }
        if (fare) {
          parts.push(`The fare is £${fare}.`);
        }
        if (booking_ref) {
          parts.push(`Your booking reference is ${booking_ref}.`);
        }
        parts.push("Is there anything else I can help you with?");
        
        confirmationMessage = parts.join(" ");
      } else if (status === "no_cars") {
        bookingStatus = "no_cars";
        confirmationMessage = message || "I'm really sorry, but we don't have any cars available at the moment. Would you like me to try again in a few minutes, or can I help with anything else?";
      } else if (status === "rejected") {
        bookingStatus = "rejected";
        confirmationMessage = message || "I'm sorry, but we're unable to take this booking at the moment. Is there anything else I can help you with?";
      } else {
        bookingStatus = "pending";
        confirmationMessage = message || "Your booking is being processed. Please hold on a moment.";
      }

      // Update live_calls with dispatch response
      const { error: updateError } = await supabase
        .from("live_calls")
        .update({
          status: bookingStatus,
          eta: normalizedEta || null,
          fare: fare || null,
          updated_at: new Date().toISOString()
        })
        .eq("call_id", call_id);

      if (updateError) {
        console.error(`[${call_id}] ❌ Failed to update live_calls:`, updateError);
      }

      // Update bookings table if dispatched
      if (status === "dispatched") {
        const bookingUpdate: Record<string, any> = {
          status: "dispatched",
          updated_at: new Date().toISOString()
        };
        if (normalizedEta) bookingUpdate.eta = normalizedEta;
        if (fare) bookingUpdate.fare = fare;
        if (driver_name || vehicle_reg || booking_ref) {
          bookingUpdate.booking_details = {
            driver_name: driver_name || null,
            vehicle_reg: vehicle_reg || null,
            vehicle_type: vehicle_type || null,
            booking_ref: booking_ref || null,
            dispatched_at: new Date().toISOString()
          };
        }

        await supabase
          .from("bookings")
          .update(bookingUpdate)
          .eq("call_id", call_id);
      }

      // Broadcast dispatch update to live calls page AND to taxi-realtime
      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: "dispatch_confirm",
        payload: {
          call_id,
          action: "confirm",
          status,
          eta: normalizedEta,
          eta_minutes,
          driver_name,
          vehicle_reg,
          fare,
          booking_ref,
          confirmation_message: confirmationMessage
        }
      });

      console.log(`[${call_id}] ✅ Dispatch confirm processed: ${status}`);

      return new Response(JSON.stringify({
        success: true,
        call_id,
        action: "confirm",
        status: bookingStatus,
        confirmation_message: confirmationMessage
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // Unknown action
    return new Response(JSON.stringify({ 
      error: `Unknown action: ${action}` 
    }), {
      status: 400,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });

  } catch (error) {
    console.error("Dispatch callback error:", error);
    return new Response(JSON.stringify({ 
      error: error instanceof Error ? error.message : "Unknown error" 
    }), {
      status: 500,
      headers: { ...corsHeaders, "Content-Type": "application/json" },
    });
  }
});
