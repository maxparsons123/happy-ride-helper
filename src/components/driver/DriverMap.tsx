import { useEffect, useRef } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { DriverCoords } from '@/hooks/use-driver-state';

const BLACK_CAB_SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 58 36" width="58" height="36"><rect x="4" y="14" width="50" height="14" rx="3" ry="3" fill="#000"/><path d="M18 14v-4c0-3 2-5 5-5h12c3 0 5 2 5 5v4h-22z" fill="#000"/><rect x="25" y="4" width="8" height="3" rx="1" fill="#FFD700" stroke="#000" stroke-width="0.5"/><text x="29" y="6.6" text-anchor="middle" font-size="2.2" font-weight="bold" fill="#000" font-family="Arial, sans-serif">TAXI</text><rect x="21" y="7" width="6" height="6" rx="1" fill="#fff"/><rect x="31" y="7" width="6" height="6" rx="1" fill="#fff"/><circle cx="15" cy="28" r="4" fill="#fff" stroke="#000" stroke-width="1"/><circle cx="43" cy="28" r="4" fill="#fff" stroke="#000" stroke-width="1"/></svg>`;

function createCabIcon(heading = 0) {
  return L.divIcon({
    html: `<div style="transform:rotate(${heading}deg);transform-origin:center center;">${BLACK_CAB_SVG}</div>`,
    iconSize: [58, 36],
    iconAnchor: [29, 18],
    className: '',
  });
}

interface DriverMapProps {
  coords: DriverCoords | null;
}

export function DriverMap({ coords }: DriverMapProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const markerRef = useRef<L.Marker | null>(null);

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
    mapRef.current.panTo([coords.lat, coords.lng], { animate: true });
  }, [coords]);

  return <div ref={containerRef} className="absolute inset-0" />;
}
