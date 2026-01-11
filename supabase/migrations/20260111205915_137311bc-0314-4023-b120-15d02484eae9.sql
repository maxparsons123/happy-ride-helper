-- Create a table for known UK towns and cities
CREATE TABLE public.uk_locations (
  id uuid NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  name text NOT NULL UNIQUE,
  type text NOT NULL DEFAULT 'city',
  parent_city text,
  lat double precision,
  lng double precision,
  postcodes text[] DEFAULT ARRAY[]::text[],
  aliases text[] DEFAULT ARRAY[]::text[],
  is_distinct boolean DEFAULT true,
  created_at timestamp with time zone NOT NULL DEFAULT now()
);

-- Enable RLS
ALTER TABLE public.uk_locations ENABLE ROW LEVEL SECURITY;

-- Anyone can read locations
CREATE POLICY "Locations are publicly readable" 
ON public.uk_locations 
FOR SELECT 
USING (true);

-- Service role can manage
CREATE POLICY "Service role can manage locations" 
ON public.uk_locations 
FOR ALL 
USING (true)
WITH CHECK (true);

-- Create indexes
CREATE INDEX idx_uk_locations_name ON public.uk_locations(name);
CREATE INDEX idx_uk_locations_parent ON public.uk_locations(parent_city);
CREATE INDEX idx_uk_locations_type ON public.uk_locations(type);