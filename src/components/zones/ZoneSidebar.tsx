import { useState } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';
import { Trash2, Plus, Pencil, Save, X, MapPin } from 'lucide-react';
import type { DispatchZone, ZonePoint } from '@/hooks/use-dispatch-zones';

interface Company {
  id: string;
  name: string;
  slug: string;
}

interface ZoneSidebarProps {
  zones: DispatchZone[];
  companies: Company[];
  selectedZoneId: string | null;
  drawingMode: boolean;
  editingZone: Partial<DispatchZone> | null;
  onSelectZone: (id: string | null) => void;
  onStartDraw: () => void;
  onCancelDraw: () => void;
  onStartEdit: (zone: DispatchZone) => void;
  onSave: (zone: Partial<DispatchZone> & { zone_name: string; points: ZonePoint[] }) => void;
  onDelete: (id: string) => void;
  onEditChange: (updates: Partial<DispatchZone>) => void;
}

export function ZoneSidebar({
  zones, companies, selectedZoneId, drawingMode, editingZone,
  onSelectZone, onStartDraw, onCancelDraw, onStartEdit, onSave, onDelete, onEditChange,
}: ZoneSidebarProps) {
  const selectedZone = zones.find(z => z.id === selectedZoneId);

  return (
    <div className="flex flex-col h-full bg-background border-r">
      {/* Header */}
      <div className="p-4 border-b">
        <h2 className="text-lg font-bold flex items-center gap-2">
          <MapPin className="w-5 h-5" /> Dispatch Zones
        </h2>
        <p className="text-xs text-muted-foreground mt-1">
          Draw zones to route bookings to the nearest company
        </p>
      </div>

      {/* Actions */}
      <div className="p-3 border-b flex gap-2">
        {drawingMode ? (
          <Button variant="destructive" size="sm" className="w-full" onClick={onCancelDraw}>
            <X className="w-4 h-4 mr-1" /> Cancel Drawing
          </Button>
        ) : (
          <Button size="sm" className="w-full" onClick={onStartDraw}>
            <Plus className="w-4 h-4 mr-1" /> Draw New Zone
          </Button>
        )}
      </div>

      {/* Zone list */}
      <div className="flex-1 overflow-auto">
        {zones.length === 0 && (
          <div className="p-4 text-center text-sm text-muted-foreground">
            No zones yet. Click "Draw New Zone" to create one.
          </div>
        )}
        {zones.map(zone => (
          <div
            key={zone.id}
            className={`p-3 border-b cursor-pointer hover:bg-accent/50 transition-colors ${
              selectedZoneId === zone.id ? 'bg-accent' : ''
            }`}
            onClick={() => onSelectZone(zone.id)}
          >
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <div
                  className="w-3 h-3 rounded-full border"
                  style={{ backgroundColor: zone.color_hex?.slice(0, 7) || '#e74c3c' }}
                />
                <span className="font-medium text-sm">{zone.zone_name}</span>
              </div>
              <div className="flex gap-1">
                <Button
                  variant="ghost" size="icon" className="h-7 w-7"
                  onClick={(e) => { e.stopPropagation(); onStartEdit(zone); }}
                >
                  <Pencil className="w-3.5 h-3.5" />
                </Button>
                <Button
                  variant="ghost" size="icon" className="h-7 w-7 text-destructive"
                  onClick={(e) => { e.stopPropagation(); onDelete(zone.id); }}
                >
                  <Trash2 className="w-3.5 h-3.5" />
                </Button>
              </div>
            </div>
            <div className="text-xs text-muted-foreground mt-1">
              {companies.find(c => c.id === zone.company_id)?.name || 'No company'} •{' '}
              {zone.points.length} points • Priority {zone.priority}
            </div>
          </div>
        ))}
      </div>

      {/* Edit panel */}
      {editingZone && (
        <div className="border-t p-4 space-y-3 bg-muted/30">
          <h3 className="font-semibold text-sm">
            {editingZone.id ? 'Edit Zone' : 'New Zone'}
          </h3>

          <div>
            <Label className="text-xs">Zone Name</Label>
            <Input
              value={editingZone.zone_name || ''}
              onChange={e => onEditChange({ zone_name: e.target.value })}
              placeholder="e.g. Coventry Central"
              className="h-8 text-sm"
            />
          </div>

          <div>
            <Label className="text-xs">Company</Label>
            <Select
              value={editingZone.company_id || 'none'}
              onValueChange={v => onEditChange({ company_id: v === 'none' ? null : v })}
            >
              <SelectTrigger className="h-8 text-sm">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="none">No company</SelectItem>
                {companies.map(c => (
                  <SelectItem key={c.id} value={c.id}>{c.name}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div>
            <Label className="text-xs">Color</Label>
            <Input
              type="color"
              value={editingZone.color_hex?.slice(0, 7) || '#e74c3c'}
              onChange={e => onEditChange({ color_hex: e.target.value })}
              className="h-8 w-16"
            />
          </div>

          <div>
            <Label className="text-xs">Priority (higher = checked first)</Label>
            <Input
              type="number"
              value={editingZone.priority ?? 0}
              onChange={e => onEditChange({ priority: parseInt(e.target.value) || 0 })}
              className="h-8 text-sm"
            />
          </div>

          <div className="flex items-center gap-2">
            <Switch
              checked={editingZone.is_active ?? true}
              onCheckedChange={v => onEditChange({ is_active: v })}
            />
            <Label className="text-xs">Active</Label>
          </div>

          <div className="flex gap-2">
            <Button
              size="sm" className="flex-1"
              disabled={!editingZone.zone_name || !editingZone.points?.length}
              onClick={() => onSave(editingZone as any)}
            >
              <Save className="w-4 h-4 mr-1" /> Save
            </Button>
            <Button size="sm" variant="outline" onClick={() => onSelectZone(null)}>
              Cancel
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
