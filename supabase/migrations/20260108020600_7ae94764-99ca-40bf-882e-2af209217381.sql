-- Create callers table to store customer information
CREATE TABLE public.callers (
  id uuid NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  phone_number text NOT NULL UNIQUE,
  name text,
  created_at timestamp with time zone NOT NULL DEFAULT now(),
  updated_at timestamp with time zone NOT NULL DEFAULT now(),
  total_bookings integer NOT NULL DEFAULT 0,
  last_pickup text,
  last_destination text
);

-- Enable RLS
ALTER TABLE public.callers ENABLE ROW LEVEL SECURITY;

-- Service role can manage callers
CREATE POLICY "Service role can manage callers"
ON public.callers
FOR ALL
USING (true)
WITH CHECK (true);

-- Anyone can view callers (for dashboard)
CREATE POLICY "Anyone can view callers"
ON public.callers
FOR SELECT
USING (true);

-- Add trigger for updated_at
CREATE TRIGGER update_callers_updated_at
BEFORE UPDATE ON public.callers
FOR EACH ROW
EXECUTE FUNCTION public.update_updated_at_column();