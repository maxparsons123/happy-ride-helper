import { useEffect, useState, useRef, useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import { MapContainer, TileLayer, Marker, Popup, Polyline, useMap } from "react-leaflet";
import L from "leaflet";
import "leaflet/dist/leaflet.css";
import { Car, MapPin, Clock, User, Hash } from "lucide-react";

// Fix leaflet default icons
delete (L.Icon.Default.prototype as any)._getIconUrl;
L.Icon.Default.mergeOptions({
  iconRetinaUrl: "https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-icon-2x.png",
  iconUrl: "https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-icon.png",
  shadowUrl: "https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-shadow.png",
});

const driverIcon = new L.DivIcon({
  html: `<div style="
    width:40px;height:40px;background:#1d4ed8;border-radius:50%;
    display:flex;align-items:center;justify-content:center;
    border:3px solid white;box-shadow:0 2px 8px rgba(0,0,0,0.4);
    animation: pulse-ring 1.5s ease-out infinite;
  ">
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="white" stroke-width="2">
      <path d="M5 17h2l1-3h8l1 3h2"/>
      <path d="M5 17a2 2 0 1 0 4 0"/>
      <path d="M15 17a2 2 0 1 0 4 0"/>
      <path d="M3 11l2-6h14l2 6"/>
    </svg>
  </div>`,
  className: "",
  iconSize: [40, 40],
  iconAnchor: [20, 20],
});

const pickupIcon = new L.DivIcon({
  html: `<div style="
    width:36px;height:36px;background:#16a34a;border-radius:50%;
    display:flex;align-items:center;justify-content:center;
    border:3px solid white;box-shadow:0 2px 8px rgba(0,0,0,0.4);
  ">
    <svg width="18" height="18" viewBox="0 0 24 24" fill="white">
      <path d="M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5c-1.38 0-2.5-1.12-2.5-2.5s1.12-2.5 2.5-2.5 2.5 1.12 2.5 2.5-1.12 2.5-2.5 2.5z"/>
    </svg>
  </div>`,
  className: "",
  iconSize: [36, 36],
  iconAnchor: [18, 36],
});

/** Haversine distance in metres */
function haversine(lat1: number, lon1: number, lat2: number, lon2: number) {
  const R = 6371000;
  const dLat = ((lat2 - lat1) * Math.PI) / 180;
  const dLon = ((lon2 - lon1) * Math.PI) / 180;
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos((lat1 * Math.PI) / 180) * Math.cos((lat2 * Math.PI) / 180) * Math.sin(dLon / 2) ** 2;
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

function formatDistance(m: number) {
  if (m < 200) return "Arriving now";
  const yards = Math.round(m * 1.09361);
  if (yards < 1760) return `${yards} yards away`;
  const miles = (m / 1609.34).toFixed(1);
  return `${miles} miles away`;
}

function formatEta(m: number) {
  // Assume 20mph average city speed
  const mins = Math.max(1, Math.round((m / 1609.34) / 20 * 60));
  if (mins <= 1) return "< 1 min";
  return `${mins} min`;
}

/** Auto-fit map bounds */
function FitBounds({ driverPos, pickupPos }: { driverPos: [number, number]; pickupPos: [number, number] }) {
  const map = useMap();
  const fitted = useRef(false);
  useEffect(() => {
    if (!fitted.current) {
      map.fitBounds([driverPos, pickupPos], { padding: [60, 60], maxZoom: 16 });
      fitted.current = true;
    }
  }, [map, driverPos, pickupPos]);
  return null;
}

export default function TrackDriver() {
  const [params] = useSearchParams();

  const pickupLat = parseFloat(params.get("plat") || "52.4862");
  const pickupLon = parseFloat(params.get("plon") || "-1.8904");
  const initDriverLat = parseFloat(params.get("dlat") || `${pickupLat + 0.008}`);
  const initDriverLon = parseFloat(params.get("dlon") || `${pickupLon + 0.005}`);
  const pickupAddr = params.get("paddr") || "Pickup location";
  const driverName = params.get("pname") || "Your driver";
  const vehicleReg = params.get("preg") || "";
  const jobId = params.get("job") || "";

  const [driverPos, setDriverPos] = useState<[number, number]>([initDriverLat, initDriverLon]);
  const [arrived, setArrived] = useState(false);
  const pickupPos = useMemo<[number, number]>(() => [pickupLat, pickupLon], [pickupLat, pickupLon]);

  // Simulate driver moving toward pickup
  useEffect(() => {
    if (arrived) return;
    const interval = setInterval(() => {
      setDriverPos((prev) => {
        const dist = haversine(prev[0], prev[1], pickupLat, pickupLon);
        if (dist < 30) {
          setArrived(true);
          clearInterval(interval);
          return [pickupLat, pickupLon];
        }
        // Move ~5% closer each tick + slight wobble for realism
        const factor = 0.05;
        const wobble = () => (Math.random() - 0.5) * 0.0001;
        return [
          prev[0] + (pickupLat - prev[0]) * factor + wobble(),
          prev[1] + (pickupLon - prev[1]) * factor + wobble(),
        ];
      });
    }, 2000);
    return () => clearInterval(interval);
  }, [pickupLat, pickupLon, arrived]);

  const distance = haversine(driverPos[0], driverPos[1], pickupLat, pickupLon);

  return (
    <div className="h-screen w-screen flex flex-col bg-gray-950 text-white">
      {/* Status bar */}
      <style>{`
        @keyframes pulse-ring {
          0% { box-shadow: 0 0 0 0 rgba(29,78,216,0.5); }
          70% { box-shadow: 0 0 0 12px rgba(29,78,216,0); }
          100% { box-shadow: 0 0 0 0 rgba(29,78,216,0); }
        }
      `}</style>

      {/* Map */}
      <div className="flex-1 relative">
        <MapContainer
          center={pickupPos}
          zoom={15}
          className="h-full w-full z-0"
          zoomControl={false}
        >
          <TileLayer
            attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a>'
            url="https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png"
          />
          <FitBounds driverPos={driverPos} pickupPos={pickupPos} />

          {/* Route line */}
          <Polyline
            positions={[driverPos, pickupPos]}
            pathOptions={{ color: "#3b82f6", weight: 4, dashArray: "8 8", opacity: 0.7 }}
          />

          {/* Driver */}
          <Marker position={driverPos} icon={driverIcon}>
            <Popup>{driverName} — {vehicleReg}</Popup>
          </Marker>

          {/* Pickup */}
          <Marker position={pickupPos} icon={pickupIcon}>
            <Popup>{pickupAddr}</Popup>
          </Marker>
        </MapContainer>
      </div>

      {/* Bottom info card */}
      <div className="bg-gray-900 border-t border-gray-800 p-4 space-y-3 safe-area-inset-bottom">
        {arrived ? (
          <div className="text-center py-2">
            <div className="text-2xl font-bold text-green-400">🚕 Your taxi has arrived!</div>
            <p className="text-gray-400 mt-1">Look out for {vehicleReg}</p>
          </div>
        ) : (
          <>
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <div className="w-10 h-10 bg-blue-600 rounded-full flex items-center justify-center">
                  <Car className="w-5 h-5" />
                </div>
                <div>
                  <div className="font-semibold flex items-center gap-2">
                    <User className="w-3.5 h-3.5 text-gray-400" />
                    {driverName}
                  </div>
                  {vehicleReg && (
                    <div className="text-sm text-gray-400 flex items-center gap-1">
                      <Hash className="w-3 h-3" />
                      {vehicleReg}
                    </div>
                  )}
                </div>
              </div>
              <div className="text-right">
                <div className="text-xl font-bold text-blue-400 flex items-center gap-1 justify-end">
                  <Clock className="w-4 h-4" />
                  {formatEta(distance)}
                </div>
                <div className="text-sm text-gray-400">{formatDistance(distance)}</div>
              </div>
            </div>

            <div className="flex items-center gap-2 bg-gray-800 rounded-lg p-3">
              <MapPin className="w-4 h-4 text-green-400 shrink-0" />
              <span className="text-sm truncate">{pickupAddr}</span>
            </div>
          </>
        )}

        {jobId && (
          <div className="text-center text-xs text-gray-600">
            Booking ref: {jobId}
          </div>
        )}
      </div>
    </div>
  );
}
