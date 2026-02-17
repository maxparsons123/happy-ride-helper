
-- Fix RLS: drop restrictive policies and recreate as permissive
DROP POLICY IF EXISTS "Zone POIs are publicly readable" ON public.zone_pois;
DROP POLICY IF EXISTS "Service role can manage zone POIs" ON public.zone_pois;

CREATE POLICY "Zone POIs are publicly readable"
  ON public.zone_pois FOR SELECT
  TO anon, authenticated
  USING (true);

CREATE POLICY "Service role can manage zone POIs"
  ON public.zone_pois FOR ALL
  TO service_role
  USING (true)
  WITH CHECK (true);
