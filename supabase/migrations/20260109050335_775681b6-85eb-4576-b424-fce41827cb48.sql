-- Add known_areas column to track all cities/areas a caller mentions
-- Format: {"Coventry": 5, "Birmingham": 1} - city name to mention count
ALTER TABLE public.callers ADD COLUMN known_areas jsonb DEFAULT '{}';