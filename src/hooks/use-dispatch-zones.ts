import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { supabase } from '@/integrations/supabase/client';
import { toast } from 'sonner';

export interface ZonePoint {
  lat: number;
  lng: number;
}

export interface DispatchZone {
  id: string;
  company_id: string | null;
  zone_name: string;
  color_hex: string;
  points: ZonePoint[];
  priority: number;
  is_active: boolean;
  created_at: string;
  updated_at: string;
}

export function useDispatchZones() {
  return useQuery({
    queryKey: ['dispatch-zones'],
    queryFn: async () => {
      const { data, error } = await supabase
        .from('dispatch_zones')
        .select('*')
        .order('priority', { ascending: false });
      if (error) throw error;
      return (data as any[]).map(d => ({
        ...d,
        points: (d.points || []) as ZonePoint[],
      })) as DispatchZone[];
    },
  });
}

export function useCompanies() {
  return useQuery({
    queryKey: ['companies'],
    queryFn: async () => {
      const { data, error } = await supabase
        .from('companies')
        .select('id, name, slug')
        .eq('is_active', true)
        .order('name');
      if (error) throw error;
      return data;
    },
  });
}

export function useSaveZone() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (zone: Partial<DispatchZone> & { zone_name: string; points: ZonePoint[] }) => {
      if (zone.id) {
        const { error } = await supabase
          .from('dispatch_zones')
          .update({
            zone_name: zone.zone_name,
            company_id: zone.company_id,
            color_hex: zone.color_hex,
            points: zone.points as any,
            priority: zone.priority,
            is_active: zone.is_active,
          })
          .eq('id', zone.id);
        if (error) throw error;
      } else {
        const { error } = await supabase
          .from('dispatch_zones')
          .insert({
            zone_name: zone.zone_name,
            company_id: zone.company_id || null,
            color_hex: zone.color_hex || '#FF000055',
            points: zone.points as any,
            priority: zone.priority || 0,
            is_active: zone.is_active ?? true,
          });
        if (error) throw error;
      }
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['dispatch-zones'] });
      toast.success('Zone saved');
    },
    onError: (e) => toast.error('Failed to save zone: ' + e.message),
  });
}

export function useDeleteZone() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { error } = await supabase.from('dispatch_zones').delete().eq('id', id);
      if (error) throw error;
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['dispatch-zones'] });
      toast.success('Zone deleted');
    },
    onError: (e) => toast.error('Failed to delete zone: ' + e.message),
  });
}
