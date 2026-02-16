-- Enable pg_trgm extension if not already enabled
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Function to fuzzy-match a street name within a specific city
-- Returns best matches from uk_locations and address_cache
CREATE OR REPLACE FUNCTION public.fuzzy_match_street(
  p_street_name TEXT,
  p_city TEXT,
  p_limit INT DEFAULT 5
)
RETURNS TABLE (
  source TEXT,
  matched_name TEXT,
  matched_city TEXT,
  similarity_score REAL,
  lat DOUBLE PRECISION,
  lon DOUBLE PRECISION
)
LANGUAGE plpgsql
SET search_path = public
AS $$
BEGIN
  RETURN QUERY
  -- Search uk_locations
  SELECT 
    'uk_locations'::TEXT AS source,
    ul.name AS matched_name,
    COALESCE(ul.parent_city, '') AS matched_city,
    similarity(lower(ul.name), lower(p_street_name)) AS similarity_score,
    ul.lat::DOUBLE PRECISION,
    ul.lng::DOUBLE PRECISION
  FROM uk_locations ul
  WHERE 
    (lower(ul.parent_city) = lower(p_city) OR lower(ul.name) ILIKE '%' || lower(p_city) || '%')
    AND similarity(lower(ul.name), lower(p_street_name)) > 0.2
  
  UNION ALL
  
  -- Search address_cache
  SELECT 
    'address_cache'::TEXT AS source,
    ac.display_name AS matched_name,
    COALESCE(ac.city, '') AS matched_city,
    similarity(lower(ac.display_name), lower(p_street_name)) AS similarity_score,
    ac.lat::DOUBLE PRECISION,
    ac.lon::DOUBLE PRECISION
  FROM address_cache ac
  WHERE 
    lower(ac.city) = lower(p_city)
    AND similarity(lower(ac.display_name), lower(p_street_name)) > 0.2
  
  ORDER BY similarity_score DESC
  LIMIT p_limit;
END;
$$;