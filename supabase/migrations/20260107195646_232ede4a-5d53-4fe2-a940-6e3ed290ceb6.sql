-- Create function to update timestamps
CREATE OR REPLACE FUNCTION public.update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
NEW.updated_at = now();
RETURN NEW;
END;
$$ LANGUAGE plpgsql SET search_path = public;

-- Create live_calls table for real-time call monitoring
CREATE TABLE public.live_calls (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  call_id TEXT NOT NULL UNIQUE,
  source TEXT NOT NULL DEFAULT 'web',
  status TEXT NOT NULL DEFAULT 'active',
  pickup TEXT,
  destination TEXT,
  passengers INTEGER,
  transcripts JSONB NOT NULL DEFAULT '[]'::jsonb,
  booking_confirmed BOOLEAN NOT NULL DEFAULT false,
  fare TEXT,
  eta TEXT,
  started_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  ended_at TIMESTAMP WITH TIME ZONE
);

-- Enable Row Level Security
ALTER TABLE public.live_calls ENABLE ROW LEVEL SECURITY;

-- Create policy for public read access (monitoring dashboard)
CREATE POLICY "Anyone can view live calls" 
ON public.live_calls 
FOR SELECT 
USING (true);

-- Create policy for service role to manage calls
CREATE POLICY "Service role can manage live calls" 
ON public.live_calls 
FOR ALL 
USING (true)
WITH CHECK (true);

-- Create index for active calls lookup
CREATE INDEX idx_live_calls_status ON public.live_calls(status);
CREATE INDEX idx_live_calls_call_id ON public.live_calls(call_id);

-- Enable realtime for live_calls table
ALTER PUBLICATION supabase_realtime ADD TABLE public.live_calls;

-- Create trigger for automatic updated_at
CREATE TRIGGER update_live_calls_updated_at
BEFORE UPDATE ON public.live_calls
FOR EACH ROW
EXECUTE FUNCTION public.update_updated_at_column();