-- Create table for SIP trunk configurations
CREATE TABLE public.sip_trunks (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  sip_server TEXT,
  sip_username TEXT,
  sip_password TEXT,
  webhook_token TEXT NOT NULL DEFAULT encode(gen_random_bytes(16), 'hex'),
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Enable RLS
ALTER TABLE public.sip_trunks ENABLE ROW LEVEL SECURITY;

-- Allow public read/write for now (no auth in this app)
CREATE POLICY "Allow all operations on sip_trunks" 
ON public.sip_trunks 
FOR ALL 
USING (true)
WITH CHECK (true);

-- Add trigger for updated_at
CREATE TRIGGER update_sip_trunks_updated_at
BEFORE UPDATE ON public.sip_trunks
FOR EACH ROW
EXECUTE FUNCTION public.update_updated_at_column();