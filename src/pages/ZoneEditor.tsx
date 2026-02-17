import { useState, useCallback } from 'react';
import { useDispatchZones, useCompanies, useSaveZone, useDeleteZone } from '@/hooks/use-dispatch-zones';
import type { DispatchZone, ZonePoint } from '@/hooks/use-dispatch-zones';
import { ZoneEditorMap } from '@/components/zones/ZoneEditorMap';
import { ZoneSidebar } from '@/components/zones/ZoneSidebar';
import { useLiveBookings } from '@/hooks/use-live-bookings';
import { toast } from 'sonner';

export default function ZoneEditor() {
  const { data: zones = [], isLoading } = useDispatchZones();
  const { data: companies = [] } = useCompanies();
  const saveZone = useSaveZone();
  const deleteZone = useDeleteZone();
  const liveBookings = useLiveBookings();

  const [selectedZoneId, setSelectedZoneId] = useState<string | null>(null);
  const [drawingMode, setDrawingMode] = useState(false);
  const [editingZone, setEditingZone] = useState<Partial<DispatchZone> | null>(null);
  const [pendingPoints, setPendingPoints] = useState<ZonePoint[] | null>(null);

  const handleStartDraw = () => {
    setSelectedZoneId(null);
    setEditingZone(null);
    setDrawingMode(true);
  };

  const handleCancelDraw = () => {
    setDrawingMode(false);
    setPendingPoints(null);
  };

  const handleDrawComplete = useCallback((points: ZonePoint[]) => {
    setDrawingMode(false);
    setPendingPoints(points);
    setEditingZone({
      zone_name: '',
      company_id: null,
      color_hex: '#e74c3c',
      points,
      priority: 0,
      is_active: true,
    });
  }, []);

  const handleStartEdit = (zone: DispatchZone) => {
    setDrawingMode(false);
    setSelectedZoneId(zone.id);
    setEditingZone({ ...zone });
  };

  const handleSelectZone = (id: string | null) => {
    setSelectedZoneId(id);
    if (!id) setEditingZone(null);
  };

  const handleEditChange = (updates: Partial<DispatchZone>) => {
    if (!editingZone) return;
    setEditingZone(prev => ({ ...prev, ...updates }));
  };

  const handlePointsUpdated = useCallback((zoneId: string, points: ZonePoint[]) => {
    setEditingZone(prev => {
      if (!prev || prev.id !== zoneId) return prev;
      return { ...prev, points };
    });
  }, []);

  const handleSave = (zone: Partial<DispatchZone> & { zone_name: string; points: ZonePoint[] }) => {
    if (!zone.zone_name.trim()) {
      toast.error('Zone name is required');
      return;
    }
    if (!zone.points || zone.points.length < 3) {
      toast.error('Zone needs at least 3 points');
      return;
    }
    saveZone.mutate(zone, {
      onSuccess: () => {
        setEditingZone(null);
        setPendingPoints(null);
        setSelectedZoneId(null);
      },
    });
  };

  const handleDelete = (id: string) => {
    if (!confirm('Delete this zone?')) return;
    deleteZone.mutate(id, {
      onSuccess: () => {
        if (selectedZoneId === id) {
          setSelectedZoneId(null);
          setEditingZone(null);
        }
      },
    });
  };

  // Combine saved zones with pending drawn zone for preview
  // For the zone being edited, use editingZone.points so drag changes persist visually
  const displayZones = zones.map(z => {
    if (editingZone && editingZone.id === z.id && editingZone.points) {
      return { ...z, points: editingZone.points };
    }
    return z;
  });
  if (pendingPoints && editingZone && !editingZone.id) {
    displayZones.push({
      id: '__pending__',
      company_id: editingZone.company_id || null,
      zone_name: editingZone.zone_name || 'New Zone',
      color_hex: editingZone.color_hex || '#2ecc71',
      points: editingZone.points || pendingPoints,
      priority: 0,
      is_active: true,
      created_at: '',
      updated_at: '',
    });
  }

  if (isLoading) {
    return (
      <div className="h-screen flex items-center justify-center bg-background">
        <div className="text-muted-foreground">Loading zones...</div>
      </div>
    );
  }

  return (
    <div className="h-screen flex">
      {/* Sidebar */}
      <div className="w-80 flex-shrink-0">
        <ZoneSidebar
          zones={zones}
          companies={companies}
          selectedZoneId={selectedZoneId}
          drawingMode={drawingMode}
          editingZone={editingZone}
          onSelectZone={handleSelectZone}
          onStartDraw={handleStartDraw}
          onCancelDraw={handleCancelDraw}
          onStartEdit={handleStartEdit}
          onSave={handleSave}
          onDelete={handleDelete}
          onEditChange={handleEditChange}
        />
      </div>

      {/* Map */}
      <div className="flex-1 relative">
        <ZoneEditorMap
          zones={displayZones}
          selectedZoneId={selectedZoneId || (pendingPoints ? '__pending__' : null)}
          isEditing={!!editingZone}
          drawingMode={drawingMode}
          onDrawComplete={handleDrawComplete}
          onZoneSelect={(id) => {
            if (id === '__pending__') return;
            handleSelectZone(id);
          }}
          onPointsUpdated={handlePointsUpdated}
          liveBookings={liveBookings}
        />
      </div>
    </div>
  );
}
