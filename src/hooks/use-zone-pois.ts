import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { supabase } from '@/integrations/supabase/client';
import { toast } from 'sonner';
import type { ZonePoint } from './use-dispatch-zones';

export interface ZonePoi {
  id: string;
  zone_id: string;
  poi_type: 'street' | 'business';
  name: string;
  lat: number | null;
  lng: number | null;
  osm_id: number | null;
  created_at: string;
}

export function useZonePois(zoneId: string | null) {
  return useQuery({
    queryKey: ['zone-pois', zoneId],
    queryFn: async () => {
      if (!zoneId) return [];
      const { data, error } = await supabase
        .from('zone_pois')
        .select('*')
        .eq('zone_id', zoneId)
        .order('poi_type')
        .order('name');
      if (error) throw error;
      return data as ZonePoi[];
    },
    enabled: !!zoneId,
  });
}

export function useFetchZonePois() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ zoneId, points }: { zoneId: string; points: ZonePoint[] }) => {
      const { data, error } = await supabase.functions.invoke('zone-pois', {
        body: { zone_id: zoneId, points },
      });
      if (error) throw error;
      if (data?.error) throw new Error(data.error);
      return data as { success: boolean; streets: number; businesses: number; total: number };
    },
    onSuccess: (data, vars) => {
      qc.invalidateQueries({ queryKey: ['zone-pois', vars.zoneId] });
      toast.success(`Found ${data.streets} streets and ${data.businesses} businesses`);
    },
    onError: (e) => toast.error('Failed to fetch POIs: ' + e.message),
  });
}
