-- Create companies table
CREATE TABLE public.companies (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  name TEXT NOT NULL,
  slug TEXT NOT NULL UNIQUE,
  webhook_url TEXT,
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Enable RLS
ALTER TABLE public.companies ENABLE ROW LEVEL SECURITY;

-- RLS policies
CREATE POLICY "Companies are publicly readable" 
ON public.companies FOR SELECT USING (true);

CREATE POLICY "Service role can manage companies" 
ON public.companies FOR ALL USING (true) WITH CHECK (true);

-- Add company_id to bookings
ALTER TABLE public.bookings 
ADD COLUMN company_id UUID REFERENCES public.companies(id);

-- Add company_id to live_calls
ALTER TABLE public.live_calls 
ADD COLUMN company_id UUID REFERENCES public.companies(id);

-- Create index for faster filtering
CREATE INDEX idx_bookings_company_id ON public.bookings(company_id);
CREATE INDEX idx_live_calls_company_id ON public.live_calls(company_id);

-- Trigger for updated_at
CREATE TRIGGER update_companies_updated_at
BEFORE UPDATE ON public.companies
FOR EACH ROW
EXECUTE FUNCTION public.update_updated_at_column();

-- Insert a default company
INSERT INTO public.companies (name, slug) VALUES ('247 Radio Carz', 'default');