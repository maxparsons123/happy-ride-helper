-- Create address cache table for local fuzzy matching
CREATE TABLE public.address_cache (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  raw_input text NOT NULL,           -- Original user input: "cosy club coventry"
  normalized text NOT NULL,          -- Lowercase, trimmed for matching
  display_name text NOT NULL,        -- Google's corrected version: "Cosy Club, 12 High Street, Coventry"
  city text,                         -- Extracted city for filtering
  lat double precision,
  lon double precision,
  use_count integer DEFAULT 1,
  created_at timestamptz DEFAULT now(),
  last_used_at timestamptz DEFAULT now()
);

-- Enable trigram extension for fuzzy matching
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Create trigram index for fast fuzzy searches
CREATE INDEX idx_address_cache_trgm ON public.address_cache USING GIN (normalized gin_trgm_ops);

-- Create index for city-scoped searches
CREATE INDEX idx_address_cache_city ON public.address_cache (city);

-- Create unique constraint on normalized input + city to avoid duplicates
CREATE UNIQUE INDEX idx_address_cache_unique ON public.address_cache (normalized, city) WHERE city IS NOT NULL;
CREATE UNIQUE INDEX idx_address_cache_unique_nocity ON public.address_cache (normalized) WHERE city IS NULL;

-- Enable RLS
ALTER TABLE public.address_cache ENABLE ROW LEVEL SECURITY;

-- Allow service role full access
CREATE POLICY "Service role can manage address cache"
ON public.address_cache
FOR ALL
USING (true)
WITH CHECK (true);

-- Allow read access for lookups
CREATE POLICY "Anyone can read address cache"
ON public.address_cache
FOR SELECT
USING (true);