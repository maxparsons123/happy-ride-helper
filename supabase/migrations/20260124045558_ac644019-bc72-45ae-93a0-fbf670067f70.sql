-- Add confirmation_asked_at column to live_calls for silent reconnect during summary phase
-- This tracks when Ada asked "Is that correct?" so we can resume correctly after a WebSocket timeout
ALTER TABLE public.live_calls 
ADD COLUMN IF NOT EXISTS confirmation_asked_at TIMESTAMP WITH TIME ZONE;