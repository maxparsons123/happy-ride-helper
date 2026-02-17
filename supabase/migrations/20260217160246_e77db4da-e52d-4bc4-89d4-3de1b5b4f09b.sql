
-- Dispatch zones: geographic polygons linked to companies
CREATE TABLE public.dispatch_zones (
  id UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
  company_id UUID REFERENCES public.companies(id) ON DELETE CASCADE,
  zone_name TEXT NOT NULL,
  color_hex TEXT NOT NULL DEFAULT '#FF000055',
  -- Store polygon as array of {lat, lng} points
  points JSONB NOT NULL DEFAULT '[]'::jsonb,
  priority INTEGER NOT NULL DEFAULT 0,
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE public.dispatch_zones ENABLE ROW LEVEL SECURITY;

CREATE POLICY "Zones are publicly readable" ON public.dispatch_zones FOR SELECT USING (true);
CREATE POLICY "Service role can manage zones" ON public.dispatch_zones FOR ALL USING (true) WITH CHECK (true);

CREATE TRIGGER update_dispatch_zones_updated_at
  BEFORE UPDATE ON public.dispatch_zones
  FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();

-- Function to find which zone a point falls in (ray-casting)
CREATE OR REPLACE FUNCTION public.find_zone_for_point(p_lat DOUBLE PRECISION, p_lng DOUBLE PRECISION)
RETURNS TABLE(zone_id UUID, company_id UUID, zone_name TEXT, priority INTEGER)
LANGUAGE plpgsql SET search_path TO 'public'
AS $$
DECLARE
  z RECORD;
  points JSONB;
  n INTEGER;
  i INTEGER;
  j INTEGER;
  xi DOUBLE PRECISION;
  yi DOUBLE PRECISION;
  xj DOUBLE PRECISION;
  yj DOUBLE PRECISION;
  inside BOOLEAN;
BEGIN
  FOR z IN SELECT dz.id, dz.company_id, dz.zone_name, dz.points, dz.priority
           FROM dispatch_zones dz WHERE dz.is_active = true ORDER BY dz.priority DESC
  LOOP
    points := z.points;
    n := jsonb_array_length(points);
    IF n < 3 THEN CONTINUE; END IF;
    
    inside := false;
    j := n - 1;
    FOR i IN 0..n-1 LOOP
      xi := (points->i->>'lat')::DOUBLE PRECISION;
      yi := (points->i->>'lng')::DOUBLE PRECISION;
      xj := (points->j->>'lat')::DOUBLE PRECISION;
      yj := (points->j->>'lng')::DOUBLE PRECISION;
      
      IF ((yi > p_lng) != (yj > p_lng)) AND
         (p_lat < (xj - xi) * (p_lng - yi) / (yj - yi) + xi) THEN
        inside := NOT inside;
      END IF;
      j := i;
    END LOOP;
    
    IF inside THEN
      zone_id := z.id;
      company_id := z.company_id;
      zone_name := z.zone_name;
      priority := z.priority;
      RETURN NEXT;
    END IF;
  END LOOP;
END;
$$;
