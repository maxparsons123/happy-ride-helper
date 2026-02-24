import { useEffect, useRef } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { DriverCoords, JobData } from '@/hooks/use-driver-state';

const BLACK_CAB_SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 58 36" width="58" height="36"><rect x="4" y="14" width="50" height="14" rx="3" ry="3" fill="#000"/><path d="M18 14v-4c0-3 2-5 5-5h12c3 0 5 2 5 5v4h-22z" fill="#000"/><rect x="25" y="4" width="8" height="3" rx="1" fill="#FFD700" stroke="#000" stroke-width="0.5"/><text x="29" y="6.6" text-anchor="middle" font-size="2.2" font-weight="bold" fill="#000" font-family="Arial, sans-serif">TAXI</text><rect x="21" y="7" width="6" height="6" rx="1" fill="#fff"/><rect x="31" y="7" width="6" height="6" rx="1" fill="#fff"/><circle cx="15" cy="28" r="4" fill="#fff" stroke="#000" stroke-width="1"/><circle cx="43" cy="28" r="4" fill="#fff" stroke="#000" stroke-width="1"/></svg>`;

function createCabIcon(heading = 0) {
  return L.divIcon({
    html: `<div style="transform:rotate(${heading}deg);transform-origin:center center;">${BLACK_CAB_SVG}</div>`,
    iconSize: [58, 36],
    iconAnchor: [29, 18],
    className: '',
  });
}

const PICKUP_SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 36" width="28" height="42"><path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="#e53e3e"/><circle cx="12" cy="12" r="5" fill="#fff"/></svg>`;
const DROPOFF_SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 36" width="28" height="42"><path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="#38a169"/><circle cx="12" cy="12" r="5" fill="#fff"/></svg>`;

function createPinIcon(svg: string) {
  return L.divIcon({
    html: svg,
    iconSize: [28, 42],
    iconAnchor: [14, 42],
    popupAnchor: [0, -42],
    className: '',
  });
}

interface DriverMapProps {
  coords: DriverCoords | null;
  allocatedJob?: JobData | null;
}

export function DriverMap({ coords, allocatedJob }: DriverMapProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const markerRef = useRef<L.Marker | null>(null);
  const pickupMarkerRef = useRef<L.Marker | null>(null);
  const dropoffMarkerRef = useRef<L.Marker | null>(null);
  const routeLayerRef = useRef<L.GeoJSON | null>(null);

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

    markerRef.current = L.marker([52.4068, -1.5197], { icon: createCabIcon(0) }).addTo(map);
    mapRef.current = map;

    return () => { map.remove(); mapRef.current = null; };
  }, []);

  useEffect(() => {
    if (!coords || !mapRef.current || !markerRef.current) return;
    markerRef.current.setLatLng([coords.lat, coords.lng]);
    markerRef.current.setIcon(createCabIcon(coords.heading));
    if (!allocatedJob) {
      mapRef.current.panTo([coords.lat, coords.lng], { animate: true });
    }
  }, [coords, allocatedJob]);

  // Show pickup/dropoff markers and route for allocated job
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    // Clear previous markers/route
    if (pickupMarkerRef.current) { pickupMarkerRef.current.remove(); pickupMarkerRef.current = null; }
    if (dropoffMarkerRef.current) { dropoffMarkerRef.current.remove(); dropoffMarkerRef.current = null; }
    if (routeLayerRef.current) { routeLayerRef.current.remove(); routeLayerRef.current = null; }

    if (!allocatedJob || !allocatedJob.lat || !allocatedJob.lng) return;

    // Pickup marker
    pickupMarkerRef.current = L.marker([allocatedJob.lat, allocatedJob.lng], { icon: createPinIcon(PICKUP_SVG) })
      .addTo(map)
      .bindPopup(`📍 Pickup: ${allocatedJob.pickupAddress}`);

    // Dropoff marker
    if (allocatedJob.dropoffLat && allocatedJob.dropoffLng && Math.abs(allocatedJob.dropoffLat) > 0.01) {
      dropoffMarkerRef.current = L.marker([allocatedJob.dropoffLat, allocatedJob.dropoffLng], { icon: createPinIcon(DROPOFF_SVG) })
        .addTo(map)
        .bindPopup(`🏁 Dropoff: ${allocatedJob.dropoff}`);
    }

    // Fit bounds to show driver + pickup
    const bounds = L.latLngBounds([[allocatedJob.lat, allocatedJob.lng]]);
    if (coords) bounds.extend([coords.lat, coords.lng]);
    if (dropoffMarkerRef.current) bounds.extend([allocatedJob.dropoffLat, allocatedJob.dropoffLng]);
    map.fitBounds(bounds, { padding: [60, 60] });

    // Fetch OSRM route from driver to pickup
    if (coords) {
      fetch(`https://router.project-osrm.org/route/v1/driving/${coords.lng},${coords.lat};${allocatedJob.lng},${allocatedJob.lat}?overview=full&geometries=geojson`)
        .then(r => r.json())
        .then(data => {
          if (data.routes?.[0]?.geometry) {
            routeLayerRef.current = L.geoJSON(data.routes[0].geometry, {
              style: { color: '#3b82f6', weight: 5, opacity: 0.8 },
            }).addTo(map);
          }
        })
        .catch(() => {});
    }
  }, [allocatedJob?.jobId, allocatedJob?.lat, allocatedJob?.lng, coords?.lat, coords?.lng]);

  return <div ref={containerRef} className="absolute inset-0" />;
}
