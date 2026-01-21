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
 * 3. ASK_CONFIRM - Ask customer to confirm fare before dispatching:
 * {
 *   "call_id": "abc123",
 *   "action": "ask_confirm",
 *   "message": "Your price is £15 and driver will be 15 minutes. Shall I book that?",
 *   "fare": "15.00",
 *   "eta": "15 minutes",
 *   "callback_url": "https://your-server.com/ada-response"  // optional
 * }
 * → If customer says YES → dispatches booking
 * → If customer says NO → sends cancel to callback_url
 * 
 * 4. SAY MESSAGE - Make Ada say something (no response expected):
 * {
 *   "call_id": "abc123",
 *   "action": "say",
 *   "message": "Just to let you know, your driver John is running 2 minutes late."
 * }
 * 
 * 5. REJECT/NO CARS:
 * {
 *   "call_id": "abc123",
 *   "action": "confirm",
 *   "status": "rejected" | "no_cars",
 *   "message": "Sorry, we can't service that area."
 * }
 * 
 * CALL ACTIONS (use "call_action" field):
 * 
 * 6. HANGUP - Instruct Ada to end the call:
 * {
 *   "call_id": "abc123",
 *   "call_action": "hangup",
 *   "message": "Goodbye!"  // optional - Ada will say this before hanging up
 * }
 */

interface DispatchCallback {
  call_id: string;
  event?: "booking_modified"; // For modification callbacks
  action?: "confirm" | "ask" | "ask_confirm" | "say" | "booked" | "update_fare"; // "booked" is alias for "confirm"
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
  ignored?: boolean; // Flag to mark questions that don't need processing
  // For ask_confirm action - fare confirmation with callback
  callback_url?: string; // URL to POST yes/no response
  // For say action (uses message field)
  // For hangup call_action - optional goodbye message
  // For booking_modified event
  field_changed?: string;
  old_value?: string;
  new_value?: string;
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
      event,
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
      context,
      ignored,
      callback_url,
      field_changed,
      old_value,
      new_value
    } = callback;
    
    // Normalize ETA: prefer eta_minutes if provided
    const normalizedEta = eta_minutes ? `${eta_minutes} minutes` : eta;

    console.log(`[${call_id}] 📥 Dispatch callback: event=${event}, action=${action}, call_action=${call_action}`);
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
    // ACTION: UPDATE_FARE - Update fare/eta after booking modification
    // ═══════════════════════════════════════════════════════════════════
    if (action === "update_fare") {
      console.log(`[${call_id}] 💰 Fare update: fare=${fare}, eta=${normalizedEta}`);

      // Update live_calls with new fare/eta
      const updateData: Record<string, any> = {
        updated_at: new Date().toISOString()
      };
      if (fare) updateData.fare = fare;
      if (normalizedEta) updateData.eta = normalizedEta;

      const { error: updateError } = await supabase
        .from("live_calls")
        .update(updateData)
        .eq("call_id", call_id);

      if (updateError) {
        console.error(`[${call_id}] ❌ Failed to update fare:`, updateError);
      }

      // Also update bookings table
      if (fare || normalizedEta) {
        const bookingUpdate: Record<string, any> = {
          updated_at: new Date().toISOString()
        };
        if (fare) bookingUpdate.fare = fare;
        if (normalizedEta) bookingUpdate.eta = normalizedEta;

        await supabase
          .from("bookings")
          .update(bookingUpdate)
          .eq("call_id", call_id);
      }

      // If message provided, add to transcripts for Ada to speak
      if (message) {
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
          .update({ transcripts, updated_at: new Date().toISOString() })
          .eq("call_id", call_id);
      }

      console.log(`[${call_id}] ✅ Fare updated: ${fare}, eta: ${normalizedEta}`);

      return new Response(JSON.stringify({
        success: true,
        call_id,
        action: "update_fare",
        fare,
        eta: normalizedEta,
        message: "Fare updated successfully"
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

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

      console.log(`[${call_id}] 🎤 Dispatch asking: "${question}" (ignored: ${ignored || false})`);

      // Broadcast the question to the active WebSocket session
      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: "dispatch_ask",
        payload: {
          call_id,
          action: "ask",
          question,
          context: context || null,
          ignored: ignored || false,
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
            ignored: ignored || false,
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
    // ACTION: ASK_CONFIRM - Ask customer to confirm fare before dispatch
    // Customer response (yes/no) is monitored by taxi-realtime-simple
    // ═══════════════════════════════════════════════════════════════════
    if (action === "ask_confirm") {
      if (!message) {
        return new Response(JSON.stringify({ 
          error: "Missing required field for 'ask_confirm' action: message" 
        }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      console.log(`[${call_id}] 💰 Dispatch ask_confirm: "${message}"`);
      console.log(`[${call_id}] Fare: ${fare}, ETA: ${normalizedEta}, Callback: ${callback_url}`);

      // Get current transcripts
      const { data: callData } = await supabase
        .from("live_calls")
        .select("transcripts")
        .eq("call_id", call_id)
        .single();

      const transcripts = (callData?.transcripts as any[]) || [];
      
      // Add the fare confirmation request with special role
      transcripts.push({
        role: "dispatch_ask_confirm",
        text: message,
        fare: fare || null,
        eta: normalizedEta || null,
        callback_url: callback_url || null,
        timestamp: new Date().toISOString()
      });

      // Update live_calls with pending confirmation state
      await supabase
        .from("live_calls")
        .update({
          transcripts,
          fare: fare || null,
          eta: normalizedEta || null,
          clarification_attempts: {
            pending_fare_confirm: true,
            fare_message: message,
            callback_url: callback_url || null,
            asked_at: new Date().toISOString()
          },
          updated_at: new Date().toISOString()
        })
        .eq("call_id", call_id);

      // Broadcast to active WebSocket session
      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: "dispatch_ask_confirm",
        payload: {
          call_id,
          action: "ask_confirm",
          message,
          fare: fare || null,
          eta: normalizedEta || null,
          callback_url: callback_url || null,
          timestamp: new Date().toISOString()
        }
      });

      return new Response(JSON.stringify({
        success: true,
        call_id,
        action: "ask_confirm",
        message: "Fare confirmation question sent to Ada",
        awaiting_response: true
      }), {
        status: 201, // 201 = Accepted, waiting for customer response
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION: SAY - Make Ada say something (or bypass AI with direct TTS)
    // Options:
    //   bypass_ai: true  → Direct TTS synthesis, skips OpenAI completely
    //   bypass_ai: false → Goes through OpenAI (default, may rephrase)
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

      // Check if bypass_ai is requested
      const bypassAi = (callback as any).bypass_ai === true;
      console.log(`[${call_id}] 📢 Dispatch saying: "${message}" (bypass_ai=${bypassAi})`);

      // Add dispatch message to transcripts so polling can detect it
      const { data: callData } = await supabase
        .from("live_calls")
        .select("transcripts")
        .eq("call_id", call_id)
        .single();

      const transcripts = (callData?.transcripts as any[]) || [];
      transcripts.push({
        role: bypassAi ? "dispatch_direct" : "dispatch",
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

      // Choose event based on bypass_ai flag
      const eventName = bypassAi ? "dispatch_say_direct" : "dispatch_say";
      
      await supabase.channel(`dispatch_${call_id}`).send({
        type: "broadcast",
        event: eventName,
        payload: {
          call_id,
          action: "say",
          message,
          bypass_ai: bypassAi,
          timestamp: new Date().toISOString()
        }
      });

      return new Response(JSON.stringify({
        success: true,
        call_id,
        action: "say",
        bypass_ai: bypassAi,
        message: bypassAi ? "Message sent directly to TTS (AI bypassed)" : "Message sent to Ada"
      }), {
        headers: { ...corsHeaders, "Content-Type": "application/json" },
      });
    }

    // ═══════════════════════════════════════════════════════════════════
    // ACTION: CONFIRM/BOOKED - Standard booking confirmation/rejection
    // "booked" is an alias for "confirm" for backwards compatibility
    // When action is "booked", default status to "dispatched"
    // ═══════════════════════════════════════════════════════════════════
    if (!action || action === "confirm" || action === "booked") {
      // For "booked" action, default to dispatched status
      const effectiveStatus = status || (action === "booked" ? "dispatched" : null);
      
      if (!effectiveStatus) {
        return new Response(JSON.stringify({ 
          error: "Missing required field for 'confirm' action: status" 
        }), {
          status: 400,
          headers: { ...corsHeaders, "Content-Type": "application/json" },
        });
      }

      // Build confirmation message based on effective status
      let confirmationMessage: string;
      let bookingStatus: string;

      if (effectiveStatus === "dispatched") {
        bookingStatus = "dispatched";
        // If message is provided, use it directly; otherwise build default message
        if (message) {
          confirmationMessage = message;
        } else {
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
        }
      } else if (effectiveStatus === "no_cars") {
        bookingStatus = "no_cars";
        confirmationMessage = message || "I'm really sorry, but we don't have any cars available at the moment. Would you like me to try again in a few minutes, or can I help with anything else?";
      } else if (effectiveStatus === "rejected") {
        bookingStatus = "rejected";
        confirmationMessage = message || "I'm sorry, but we're unable to take this booking at the moment. Is there anything else I can help you with?";
      } else {
        bookingStatus = "pending";
        confirmationMessage = message || "Your booking is being processed. Please hold on a moment.";
      }
      // Get current transcripts to append confirmation message
      const { data: callData } = await supabase
        .from("live_calls")
        .select("transcripts")
        .eq("call_id", call_id)
        .single();

      const transcripts = (callData?.transcripts as any[]) || [];
      // Add confirmation message with special role for polling to detect
      transcripts.push({
        role: "dispatch_confirm",
        text: confirmationMessage,
        fare: fare || null,
        eta: normalizedEta || null,
        booking_ref: booking_ref || null,
        status: effectiveStatus,
        timestamp: new Date().toISOString()
      });

      // Update live_calls with dispatch response AND confirmation message
      const { error: updateError } = await supabase
        .from("live_calls")
        .update({
          status: bookingStatus,
          eta: normalizedEta || null,
          fare: fare || null,
          transcripts,
          updated_at: new Date().toISOString()
        })
        .eq("call_id", call_id);

      if (updateError) {
        console.error(`[${call_id}] ❌ Failed to update live_calls:`, updateError);
      }
      
      console.log(`[${call_id}] 📝 Confirmation message stored: "${confirmationMessage}"`);

      // Update bookings table if dispatched
      if (effectiveStatus === "dispatched") {
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
          status: effectiveStatus,
          eta: normalizedEta,
          eta_minutes,
          driver_name,
          vehicle_reg,
          fare,
          booking_ref,
          confirmation_message: confirmationMessage
        }
      });

      console.log(`[${call_id}] ✅ Dispatch confirm processed: ${effectiveStatus}`);

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
