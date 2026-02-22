
-- Table to store self-service airport/station booking links
CREATE TABLE public.airport_booking_links (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  token TEXT NOT NULL UNIQUE DEFAULT encode(extensions.gen_random_bytes(16), 'hex'),
  
  -- Pre-filled from call session
  caller_name TEXT,
  caller_phone TEXT,
  pickup TEXT,
  destination TEXT,
  passengers INTEGER DEFAULT 1,
  pickup_lat DOUBLE PRECISION,
  pickup_lon DOUBLE PRECISION,
  dest_lat DOUBLE PRECISION,
  dest_lon DOUBLE PRECISION,
  call_id TEXT,
  company_id UUID REFERENCES public.companies(id),
  
  -- Filled by customer via form
  vehicle_type TEXT,
  flight_number TEXT,
  travel_datetime TIMESTAMPTZ,
  luggage_suitcases INTEGER DEFAULT 0,
  luggage_hand INTEGER DEFAULT 0,
  special_instructions TEXT,
  
  -- Return trip
  return_trip BOOLEAN DEFAULT false,
  return_datetime TIMESTAMPTZ,
  return_flight_number TEXT,
  return_discount_pct INTEGER DEFAULT 10,
  
  -- Fare quotes (populated by edge function)
  fare_quotes JSONB DEFAULT '{}'::jsonb,
  
  -- Status
  status TEXT NOT NULL DEFAULT 'pending',  -- pending, submitted, booked, expired
  submitted_at TIMESTAMPTZ,
  expires_at TIMESTAMPTZ NOT NULL DEFAULT (now() + INTERVAL '24 hours'),
  
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Enable RLS
ALTER TABLE public.airport_booking_links ENABLE ROW LEVEL SECURITY;

-- Public can read their own link by token
CREATE POLICY "Anyone can read booking links by token"
  ON public.airport_booking_links FOR SELECT USING (true);

-- Public can update their own pending link (form submission)
CREATE POLICY "Anyone can update pending booking links"
  ON public.airport_booking_links FOR UPDATE
  USING (status = 'pending')
  WITH CHECK (status IN ('pending', 'submitted'));

-- Service role can manage all
CREATE POLICY "Service role can manage booking links"
  ON public.airport_booking_links FOR ALL
  USING (true) WITH CHECK (true);

-- Trigger for updated_at
CREATE TRIGGER update_airport_booking_links_updated_at
  BEFORE UPDATE ON public.airport_booking_links
  FOR EACH ROW
  EXECUTE FUNCTION public.update_updated_at_column();
