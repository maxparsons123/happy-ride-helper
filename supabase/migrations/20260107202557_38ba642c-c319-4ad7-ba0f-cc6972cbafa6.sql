-- Create table for live audio streaming to monitoring clients
CREATE TABLE public.live_call_audio (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  call_id TEXT NOT NULL,
  audio_chunk TEXT NOT NULL, -- base64 encoded PCM audio
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Enable RLS (public read for monitoring)
ALTER TABLE public.live_call_audio ENABLE ROW LEVEL SECURITY;

-- Allow anyone to read (for monitoring dashboard)
CREATE POLICY "Anyone can read live audio" 
ON public.live_call_audio 
FOR SELECT 
USING (true);

-- Allow insert from edge functions (service role)
CREATE POLICY "Service can insert audio" 
ON public.live_call_audio 
FOR INSERT 
WITH CHECK (true);

-- Create index for fast lookups by call_id
CREATE INDEX idx_live_call_audio_call_id ON public.live_call_audio(call_id);

-- Auto-delete old audio chunks (keep only last 30 seconds worth)
CREATE OR REPLACE FUNCTION public.cleanup_old_audio()
RETURNS TRIGGER AS $$
BEGIN
  DELETE FROM public.live_call_audio 
  WHERE created_at < now() - INTERVAL '30 seconds';
  RETURN NEW;
END;
$$ LANGUAGE plpgsql SET search_path = public;

CREATE TRIGGER cleanup_audio_on_insert
AFTER INSERT ON public.live_call_audio
FOR EACH STATEMENT
EXECUTE FUNCTION public.cleanup_old_audio();

-- Enable realtime for audio streaming
ALTER PUBLICATION supabase_realtime ADD TABLE public.live_call_audio;

-- Set replica identity for realtime
ALTER TABLE public.live_call_audio REPLICA IDENTITY FULL;