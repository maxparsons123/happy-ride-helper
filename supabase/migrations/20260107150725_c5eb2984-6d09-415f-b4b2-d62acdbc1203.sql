-- Create call_logs table for Asterisk voice integration analytics
CREATE TABLE public.call_logs (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  call_id TEXT NOT NULL,
  
  -- Conversation data
  user_transcript TEXT,
  ai_response TEXT,
  
  -- Booking details
  pickup TEXT,
  destination TEXT,
  passengers INTEGER,
  estimated_fare TEXT,
  booking_status TEXT DEFAULT 'collecting',
  
  -- Latency metrics (in milliseconds)
  stt_latency_ms INTEGER,
  ai_latency_ms INTEGER,
  tts_latency_ms INTEGER,
  total_latency_ms INTEGER,
  
  -- Call metadata
  call_start_at TIMESTAMP WITH TIME ZONE,
  call_end_at TIMESTAMP WITH TIME ZONE,
  turn_number INTEGER DEFAULT 1,
  
  -- Timestamps
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Enable Row Level Security
ALTER TABLE public.call_logs ENABLE ROW LEVEL SECURITY;

-- Create policy for service role (edge functions) to insert logs
CREATE POLICY "Service role can insert call logs"
ON public.call_logs
FOR INSERT
WITH CHECK (true);

-- Create policy for service role to select logs (for analytics)
CREATE POLICY "Service role can view call logs"
ON public.call_logs
FOR SELECT
USING (true);

-- Create policy for service role to update logs
CREATE POLICY "Service role can update call logs"
ON public.call_logs
FOR UPDATE
USING (true);

-- Create index on call_id for fast lookups
CREATE INDEX idx_call_logs_call_id ON public.call_logs(call_id);

-- Create index on created_at for time-based queries
CREATE INDEX idx_call_logs_created_at ON public.call_logs(created_at DESC);

-- Create index on booking_status for filtering
CREATE INDEX idx_call_logs_booking_status ON public.call_logs(booking_status);