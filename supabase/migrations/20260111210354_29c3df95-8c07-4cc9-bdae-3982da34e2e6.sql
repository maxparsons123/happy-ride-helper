-- Add country column to uk_locations table (default to 'GB' for existing UK data)
ALTER TABLE public.uk_locations 
ADD COLUMN country text NOT NULL DEFAULT 'GB';

-- Create index for country lookups
CREATE INDEX idx_uk_locations_country ON public.uk_locations(country);

-- Comment on the table to reflect its broader purpose
COMMENT ON TABLE public.uk_locations IS 'Known towns, cities, and districts. Currently UK-focused with bypass for foreign addresses.';