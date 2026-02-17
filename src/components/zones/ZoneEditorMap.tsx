import { useEffect, useRef, useState, useCallback } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { ZonePoint, DispatchZone } from '@/hooks/use-dispatch-zones';
import type { LiveBookingMarker } from '@/hooks/use-live-bookings';

interface ZoneEditorMapProps {
  zones: DispatchZone[];
  selectedZoneId: string | null;
  drawingMode: boolean;
  onDrawComplete: (points: ZonePoint[]) => void;
  onZoneSelect: (id: string) => void;
  onPointsUpdated?: (zoneId: string, points: ZonePoint[]) => void;
  liveBookings?: LiveBookingMarker[];
}

const ZONE_COLORS = [
  '#e74c3c', '#3498db', '#2ecc71', '#f39c12', '#9b59b6',
  '#1abc9c', '#e67e22', '#34495e', '#d35400', '#27ae60',
];

function getZoneColor(index: number, hex?: string) {
  if (hex && hex.length >= 7) return hex.slice(0, 7);
  return ZONE_COLORS[index % ZONE_COLORS.length];
}

export function ZoneEditorMap({
  zones,
  selectedZoneId,
  drawingMode,
  onDrawComplete,
  onZoneSelect,
  onPointsUpdated,
  liveBookings = [],
}: ZoneEditorMapProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const layersRef = useRef<Map<string, L.Polygon>>(new Map());
  const bookingMarkersRef = useRef<Map<string, L.Marker>>(new Map());
  const drawPointsRef = useRef<ZonePoint[]>([]);
  const drawMarkersRef = useRef<L.CircleMarker[]>([]);
  const drawLineRef = useRef<L.Polyline | null>(null);
  const vertexMarkersRef = useRef<L.CircleMarker[]>([]);
  const [drawPoints, setDrawPoints] = useState<ZonePoint[]>([]);

  // Initialize map
  useEffect(() => {
    if (!containerRef.current || mapRef.current) return;
    const map = L.map(containerRef.current, {
      center: [52.4068, -1.5197],
      zoom: 13,
      zoomControl: true,
    });
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '© OpenStreetMap',
    }).addTo(map);
    mapRef.current = map;
    return () => { map.remove(); mapRef.current = null; };
  }, []);

  // Render zones as polygons
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    // Remove old layers
    layersRef.current.forEach(p => map.removeLayer(p));
    layersRef.current.clear();

    zones.forEach((zone, idx) => {
      if (zone.points.length < 3) return;
      const latlngs = zone.points.map(p => [p.lat, p.lng] as [number, number]);
      const color = getZoneColor(idx, zone.color_hex);
      const isSelected = zone.id === selectedZoneId;
      const polygon = L.polygon(latlngs, {
        color: isSelected ? '#fff' : color,
        weight: isSelected ? 3 : 2,
        fillColor: color,
        fillOpacity: isSelected ? 0.35 : 0.2,
        dashArray: isSelected ? '5,5' : undefined,
      }).addTo(map);

      // Zone label
      const center = polygon.getBounds().getCenter();
      polygon.bindTooltip(zone.zone_name, {
        permanent: true,
        direction: 'center',
        className: 'zone-label',
      });

      polygon.on('click', () => onZoneSelect(zone.id));
      layersRef.current.set(zone.id, polygon);
    });
  }, [zones, selectedZoneId, onZoneSelect]);

  // Show vertex markers + enable polygon dragging for selected zone (edit mode, not drawing)
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    // Clear old vertex markers
    vertexMarkersRef.current.forEach(m => map.removeLayer(m));
    vertexMarkersRef.current = [];

    if (drawingMode || !selectedZoneId) return;

    const zone = zones.find(z => z.id === selectedZoneId);
    if (!zone || zone.points.length < 3) return;

    // --- Whole polygon drag ---
    const polygon = layersRef.current.get(selectedZoneId);
    let polyDragging = false;
    let dragStart: L.LatLng | null = null;

    const onPolyMouseDown = (e: L.LeafletMouseEvent) => {
      L.DomEvent.stopPropagation(e);
      polyDragging = true;
      dragStart = e.latlng;
      map.dragging.disable();
      map.getContainer().style.cursor = 'move';
    };

    const onMapMouseMove = (e: L.LeafletMouseEvent) => {
      if (!polyDragging || !dragStart || !polygon) return;
      const dlat = e.latlng.lat - dragStart.lat;
      const dlng = e.latlng.lng - dragStart.lng;
      dragStart = e.latlng;

      const latlngs = (polygon.getLatLngs()[0] as L.LatLng[]).map(
        ll => L.latLng(ll.lat + dlat, ll.lng + dlng)
      );
      polygon.setLatLngs(latlngs);

      // Update label position
      const newCenter = polygon.getBounds().getCenter();
      polygon.closeTooltip();
      polygon.setTooltipContent(polygon.getTooltip()?.getContent() as string);
      polygon.openTooltip(newCenter);

      // Move vertex markers too
      vertexMarkersRef.current.forEach(m => {
        const pos = m.getLatLng();
        m.setLatLng(L.latLng(pos.lat + dlat, pos.lng + dlng));
      });
    };

    const onMapMouseUp = () => {
      if (!polyDragging) return;
      polyDragging = false;
      dragStart = null;
      map.dragging.enable();
      map.getContainer().style.cursor = '';

      if (onPointsUpdated && polygon) {
        const latlngs = polygon.getLatLngs()[0] as L.LatLng[];
        const newPoints = latlngs.map(ll => ({ lat: ll.lat, lng: ll.lng }));
        onPointsUpdated(selectedZoneId, newPoints);
      }
    };

    if (polygon) {
      polygon.on('mousedown', onPolyMouseDown);
    }
    map.on('mousemove', onMapMouseMove);
    map.on('mouseup', onMapMouseUp);

    // --- Vertex markers ---
    zone.points.forEach((pt, idx) => {
      const marker = L.circleMarker([pt.lat, pt.lng], {
        radius: 7,
        color: '#fff',
        fillColor: '#e74c3c',
        fillOpacity: 1,
        weight: 2,
      }).addTo(map);

      const el = marker.getElement() as HTMLElement | undefined;
      if (el) {
        el.style.cursor = 'grab';
        let dragging = false;

        const onMouseMove = (e: MouseEvent) => {
          if (!dragging) return;
          const pt = map.containerPointToLatLng(L.point(e.clientX - map.getContainer().getBoundingClientRect().left, e.clientY - map.getContainer().getBoundingClientRect().top));
          marker.setLatLng(pt);

          const polygon = layersRef.current.get(selectedZoneId);
          if (polygon) {
            const latlngs = polygon.getLatLngs()[0] as L.LatLng[];
            if (latlngs[idx]) {
              latlngs[idx] = pt;
              polygon.setLatLngs(latlngs);
            }
          }
        };

        const onMouseUp = () => {
          if (!dragging) return;
          dragging = false;
          map.dragging.enable();
          document.removeEventListener('mousemove', onMouseMove);
          document.removeEventListener('mouseup', onMouseUp);
          el.style.cursor = 'grab';

          if (onPointsUpdated) {
            const polygon = layersRef.current.get(selectedZoneId);
            if (polygon) {
              const latlngs = polygon.getLatLngs()[0] as L.LatLng[];
              const newPoints = latlngs.map(ll => ({ lat: ll.lat, lng: ll.lng }));
              onPointsUpdated(selectedZoneId, newPoints);
            }
          }
        };

        el.addEventListener('mousedown', (e) => {
          e.stopPropagation();
          dragging = true;
          map.dragging.disable();
          el.style.cursor = 'grabbing';
          document.addEventListener('mousemove', onMouseMove);
          document.addEventListener('mouseup', onMouseUp);
        });
      }

      vertexMarkersRef.current.push(marker);
    });

    return () => {
      if (polygon) {
        polygon.off('mousedown', onPolyMouseDown);
      }
      map.off('mousemove', onMapMouseMove);
      map.off('mouseup', onMapMouseUp);
    };
  }, [zones, selectedZoneId, drawingMode, onPointsUpdated]);

  // Drawing mode
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    if (!drawingMode) {
      // Clear drawing artifacts
      drawMarkersRef.current.forEach(m => map.removeLayer(m));
      drawMarkersRef.current = [];
      if (drawLineRef.current) { map.removeLayer(drawLineRef.current); drawLineRef.current = null; }
      drawPointsRef.current = [];
      setDrawPoints([]);
      return;
    }

    map.getContainer().style.cursor = 'crosshair';

    const onClick = (e: L.LeafletMouseEvent) => {
      const pt: ZonePoint = { lat: e.latlng.lat, lng: e.latlng.lng };
      drawPointsRef.current.push(pt);
      setDrawPoints([...drawPointsRef.current]);

      const marker = L.circleMarker(e.latlng, {
        radius: 6,
        color: '#2ecc71',
        fillColor: '#2ecc71',
        fillOpacity: 1,
      }).addTo(map);
      drawMarkersRef.current.push(marker);

      // Update preview line
      if (drawLineRef.current) map.removeLayer(drawLineRef.current);
      if (drawPointsRef.current.length >= 2) {
        const latlngs = drawPointsRef.current.map(p => [p.lat, p.lng] as [number, number]);
        latlngs.push(latlngs[0]); // close
        drawLineRef.current = L.polyline(latlngs, { color: '#2ecc71', weight: 2, dashArray: '5,5' }).addTo(map);
      }
    };

    const onRightClick = (e: L.LeafletMouseEvent) => {
      e.originalEvent.preventDefault();
      if (drawPointsRef.current.length >= 3) {
        onDrawComplete([...drawPointsRef.current]);
        // Clean up
        drawMarkersRef.current.forEach(m => map.removeLayer(m));
        drawMarkersRef.current = [];
        if (drawLineRef.current) { map.removeLayer(drawLineRef.current); drawLineRef.current = null; }
        drawPointsRef.current = [];
        setDrawPoints([]);
      }
    };

    map.on('click', onClick);
    map.on('contextmenu', onRightClick);

    return () => {
      map.off('click', onClick);
      map.off('contextmenu', onRightClick);
      map.getContainer().style.cursor = '';
    };
  }, [drawingMode, onDrawComplete]);

  // Render live booking markers
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    // Remove stale markers
    const currentIds = new Set(liveBookings.map(b => b.id));
    bookingMarkersRef.current.forEach((marker, id) => {
      if (!currentIds.has(id)) {
        map.removeLayer(marker);
        bookingMarkersRef.current.delete(id);
      }
    });

    // Add/update markers
    liveBookings.forEach(booking => {
      const existing = bookingMarkersRef.current.get(booking.id);
      if (existing) {
        existing.setLatLng([booking.lat, booking.lng]);
        return;
      }

      // Color based on age: green < 5min, amber 5-15min, red > 15min
      const ageMs = Date.now() - new Date(booking.created_at).getTime();
      const ageMin = ageMs / 60000;
      const bgColor = ageMin < 5 ? '#2ecc71' : ageMin < 15 ? '#f39c12' : '#e74c3c';
      const pulseColor = ageMin < 5 ? 'rgba(46,204,113,0.5)' : ageMin < 15 ? 'rgba(243,156,18,0.5)' : 'rgba(231,76,60,0.5)';
      const animName = ageMin < 5 ? 'pulse-green' : ageMin < 15 ? 'pulse-amber' : 'pulse-red';

      const passengerIcon = L.divIcon({
        html: `<div style="
          background: ${bgColor};
          border: 2px solid #fff;
          border-radius: 50%;
          width: 32px;
          height: 32px;
          display: flex;
          align-items: center;
          justify-content: center;
          font-weight: bold;
          font-size: 14px;
          color: #fff;
          box-shadow: 0 2px 8px rgba(0,0,0,0.3);
          animation: ${animName} 1.5s ease-out infinite;
        "><span>👤</span><span style="
          position: absolute;
          top: -4px;
          right: -4px;
          background: #222;
          color: white;
          font-size: 10px;
          font-weight: bold;
          width: 16px;
          height: 16px;
          border-radius: 50%;
          display: flex;
          align-items: center;
          justify-content: center;
        ">${booking.passengers}</span></div>`,
        iconSize: [32, 32],
        iconAnchor: [16, 16],
        className: '',
      });

      const marker = L.marker([booking.lat, booking.lng], { icon: passengerIcon })
        .addTo(map)
        .bindPopup(`
          <div style="min-width:160px">
            <strong>${booking.caller_name || 'Passenger'}</strong><br/>
            <small>📍 ${booking.pickup || 'Unknown'}</small><br/>
            <small>🎯 ${booking.destination || 'Unknown'}</small><br/>
            <small>👥 ${booking.passengers} pax</small>
          </div>
        `);

      bookingMarkersRef.current.set(booking.id, marker);
    });
  }, [liveBookings]);

  return (
    <div className="relative w-full h-full">
      <div ref={containerRef} className="absolute inset-0" />
      {drawingMode && (
        <div className="absolute top-3 left-1/2 -translate-x-1/2 z-[1000] bg-black/80 text-white px-4 py-2 rounded-lg text-sm font-medium">
          Click to add points • Right-click to close polygon ({drawPoints.length} points)
        </div>
      )}
      <style>{`
        .zone-label {
          background: rgba(0,0,0,0.7) !important;
          border: none !important;
          color: white !important;
          font-size: 11px !important;
          font-weight: 600 !important;
          padding: 2px 6px !important;
          border-radius: 4px !important;
          box-shadow: none !important;
        }
        .zone-label::before { display: none !important; }
        @keyframes pulse-green {
          0% { box-shadow: 0 0 0 0 rgba(46,204,113,0.5); }
          70% { box-shadow: 0 0 0 12px rgba(46,204,113,0); }
          100% { box-shadow: 0 0 0 0 rgba(46,204,113,0); }
        }
        @keyframes pulse-amber {
          0% { box-shadow: 0 0 0 0 rgba(243,156,18,0.5); }
          70% { box-shadow: 0 0 0 12px rgba(243,156,18,0); }
          100% { box-shadow: 0 0 0 0 rgba(243,156,18,0); }
        }
        @keyframes pulse-red {
          0% { box-shadow: 0 0 0 0 rgba(231,76,60,0.5); }
          70% { box-shadow: 0 0 0 12px rgba(231,76,60,0); }
          100% { box-shadow: 0 0 0 0 rgba(231,76,60,0); }
        }
      `}</style>
    </div>
  );
}
