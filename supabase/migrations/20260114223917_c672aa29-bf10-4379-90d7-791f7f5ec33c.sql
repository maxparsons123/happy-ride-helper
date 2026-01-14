-- Add GPS location columns to live_calls
ALTER TABLE public.live_calls 
ADD COLUMN IF NOT EXISTS gps_lat double precision,
ADD COLUMN IF NOT EXISTS gps_lon double precision,
ADD COLUMN IF NOT EXISTS gps_updated_at timestamp with time zone;

-- Create index for quick phone lookup
CREATE INDEX IF NOT EXISTS idx_live_calls_caller_phone ON public.live_calls(caller_phone);

-- Also create a separate table for pre-call GPS updates (before call_id exists)
CREATE TABLE IF NOT EXISTS public.caller_gps (
  id uuid NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  phone_number text NOT NULL,
  lat double precision NOT NULL,
  lon double precision NOT NULL,
  created_at timestamp with time zone NOT NULL DEFAULT now(),
  expires_at timestamp with time zone NOT NULL DEFAULT (now() + interval '10 minutes')
);

-- Enable RLS
ALTER TABLE public.caller_gps ENABLE ROW LEVEL SECURITY;

-- Allow service role full access (edge functions)
CREATE POLICY "Service role can manage caller_gps"
ON public.caller_gps
FOR ALL
USING (true)
WITH CHECK (true);

-- Index for phone lookup
CREATE INDEX IF NOT EXISTS idx_caller_gps_phone ON public.caller_gps(phone_number);

-- Auto-cleanup old GPS entries
CREATE OR REPLACE FUNCTION public.cleanup_expired_gps()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = public
AS $$
BEGIN
  DELETE FROM public.caller_gps WHERE expires_at < now();
  RETURN NEW;
END;
$$;

-- Trigger cleanup on insert
DROP TRIGGER IF EXISTS trigger_cleanup_gps ON public.caller_gps;
CREATE TRIGGER trigger_cleanup_gps
AFTER INSERT ON public.caller_gps
FOR EACH STATEMENT
EXECUTE FUNCTION public.cleanup_expired_gps();