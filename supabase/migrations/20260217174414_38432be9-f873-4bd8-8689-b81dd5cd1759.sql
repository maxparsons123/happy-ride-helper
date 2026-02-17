
-- Table to store streets and businesses discovered within each dispatch zone
CREATE TABLE public.zone_pois (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  zone_id UUID NOT NULL REFERENCES public.dispatch_zones(id) ON DELETE CASCADE,
  poi_type TEXT NOT NULL DEFAULT 'street', -- 'street' or 'business'
  name TEXT NOT NULL,
  lat DOUBLE PRECISION,
  lng DOUBLE PRECISION,
  osm_id BIGINT,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now()
);

-- Unique constraint to prevent duplicates per zone
CREATE UNIQUE INDEX idx_zone_pois_unique ON public.zone_pois(zone_id, poi_type, name, COALESCE(osm_id, 0));

-- Index for fast lookups by zone
CREATE INDEX idx_zone_pois_zone_id ON public.zone_pois(zone_id);

-- Enable RLS
ALTER TABLE public.zone_pois ENABLE ROW LEVEL SECURITY;

-- Public read access
CREATE POLICY "Zone POIs are publicly readable"
  ON public.zone_pois FOR SELECT
  USING (true);

-- Service role can manage
CREATE POLICY "Service role can manage zone POIs"
  ON public.zone_pois FOR ALL
  USING (true)
  WITH CHECK (true);
