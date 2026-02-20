-- fuzzy_match_zone_poi: match a raw address string against known zone POIs
-- using pg_trgm similarity. Returns ranked matches from zone_pois joined to
-- dispatch_zones so the caller knows which zone/company services that area.
--
-- Usage (from edge function or C# via Supabase RPC):
--   SELECT * FROM fuzzy_match_zone_poi('52A David Road', 0.25, 10);
--
-- p_address         : raw address string to match against POI names
-- p_min_similarity  : minimum trigram similarity score (0-1, default 0.25)
-- p_limit           : max rows to return (default 10)

CREATE OR REPLACE FUNCTION public.fuzzy_match_zone_poi(
    p_address        text,
    p_min_similarity real    DEFAULT 0.25,
    p_limit          integer DEFAULT 10
)
RETURNS TABLE (
    poi_id           uuid,
    poi_name         text,
    poi_type         text,
    area             text,
    zone_id          uuid,
    zone_name        text,
    company_id       uuid,
    similarity_score real,
    lat              double precision,
    lng              double precision
)
LANGUAGE plpgsql
SET search_path TO 'public'
AS $$
BEGIN
    RETURN QUERY
    SELECT
        zp.id                                              AS poi_id,
        zp.name                                            AS poi_name,
        zp.poi_type,
        zp.area,
        zp.zone_id,
        dz.zone_name,
        dz.company_id,
        similarity(lower(zp.name), lower(p_address))      AS similarity_score,
        zp.lat::DOUBLE PRECISION,
        zp.lng::DOUBLE PRECISION
    FROM  zone_pois      zp
    JOIN  dispatch_zones dz ON dz.id = zp.zone_id
    WHERE dz.is_active = true
      AND similarity(lower(zp.name), lower(p_address)) >= p_min_similarity
    ORDER BY similarity_score DESC
    LIMIT p_limit;
END;
$$;

-- Also expose a word-level variant that handles partial matches better
-- e.g. "David Road" matching "David Road, Blackburn"
CREATE OR REPLACE FUNCTION public.word_fuzzy_match_zone_poi(
    p_address        text,
    p_min_similarity real    DEFAULT 0.20,
    p_limit          integer DEFAULT 10
)
RETURNS TABLE (
    poi_id           uuid,
    poi_name         text,
    poi_type         text,
    area             text,
    zone_id          uuid,
    zone_name        text,
    company_id       uuid,
    similarity_score real,
    lat              double precision,
    lng              double precision
)
LANGUAGE plpgsql
SET search_path TO 'public'
AS $$
BEGIN
    RETURN QUERY
    SELECT
        zp.id                                                          AS poi_id,
        zp.name                                                        AS poi_name,
        zp.poi_type,
        zp.area,
        zp.zone_id,
        dz.zone_name,
        dz.company_id,
        GREATEST(
            similarity(lower(zp.name), lower(p_address)),
            word_similarity(lower(p_address), lower(zp.name))
        )                                                              AS similarity_score,
        zp.lat::DOUBLE PRECISION,
        zp.lng::DOUBLE PRECISION
    FROM  zone_pois      zp
    JOIN  dispatch_zones dz ON dz.id = zp.zone_id
    WHERE dz.is_active = true
      AND GREATEST(
            similarity(lower(zp.name), lower(p_address)),
            word_similarity(lower(p_address), lower(zp.name))
          ) >= p_min_similarity
    ORDER BY similarity_score DESC
    LIMIT p_limit;
END;
$$;