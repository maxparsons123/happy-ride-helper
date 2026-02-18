import { useEffect, useState, useRef, useCallback } from "react";
import { useSearchParams } from "react-router-dom";
import mqtt from "mqtt";

// Entirely standalone – no Tailwind, mirrors the C# HTML template exactly
const MQTT_BROKER = "wss://broker.hivemq.com:8884/mqtt";
const OSRM_BASE = "https://router.project-osrm.org/route/v1/driving";

/* ── helpers ─────────────────────────────────────────────────── */
function haversine(lat1: number, lon1: number, lat2: number, lon2: number) {
  const R = 6371000;
  const dLat = ((lat2 - lat1) * Math.PI) / 180;
  const dLon = ((lon2 - lon1) * Math.PI) / 180;
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos((lat1 * Math.PI) / 180) *
      Math.cos((lat2 * Math.PI) / 180) *
      Math.sin(dLon / 2) ** 2;
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

function speak(text: string, cancelBefore = true) {
  if (!("speechSynthesis" in window)) return;
  const msg = new SpeechSynthesisUtterance(text);
  msg.lang = "en-GB";
  msg.rate = 1;
  msg.volume = 1;
  const voices = speechSynthesis.getVoices();
  const pref =
    voices.find((v) => v.name.toLowerCase().includes("google uk")) ||
    voices.find((v) => v.lang === "en-GB") ||
    voices[0];
  if (pref) msg.voice = pref;
  if (cancelBefore) speechSynthesis.cancel();
  speechSynthesis.speak(msg);
}

/* ── ad images ────────────────────────────────────────────────── */
const AD_SLIDES = [
  "https://placehold.co/728x90/1a1a2e/e94560?text=🍕+Order+Pizza+Now+•+50%25+Off+First+Order&font=roboto",
  "https://placehold.co/728x90/0f3460/e94560?text=⚡+SuperFast+WiFi+•+From+£19.99/mo&font=roboto",
  "https://placehold.co/728x90/533483/e94560?text=🎬+StreamMax+•+Free+Trial+30+Days&font=roboto",
  "https://placehold.co/728x90/16213e/0f3460?text=🏋️+FitLife+Gym+•+No+Joining+Fee+This+Month&font=roboto",
  "https://placehold.co/728x90/1a1a2e/fbbf24?text=🚗+AutoInsure+•+Save+Up+To+40%25&font=roboto",
];

export default function TrackDriver() {
  const [params] = useSearchParams();

  const driverId = params.get("driver") || "unknown";
  const jobId = params.get("job") || "";
  const pickupLat = parseFloat(params.get("plat") || "52.4862");
  const pickupLon = parseFloat(params.get("plon") || "-1.8904");
  const initDriverLat = parseFloat(params.get("dlat") || `${pickupLat + 0.008}`);
  const initDriverLon = parseFloat(params.get("dlon") || `${pickupLon + 0.005}`);
  const pickupAddr = params.get("paddr") || "Pickup location";
  const driverName = params.get("pname") || "Your driver";
  const vehicleReg = params.get("preg") || "";

  const mapRef = useRef<HTMLDivElement>(null);
  const leafletMap = useRef<any>(null);
  const driverMarkerRef = useRef<any>(null);
  const passengerMarkerRef = useRef<any>(null);
  const routeLineRef = useRef<any>(null);
  const mqttRef = useRef<any>(null);
  const lastMqttTs = useRef(0);
  const demoRouteRef = useRef<[number, number][] | null>(null);
  const demoIndexRef = useRef(0);
  const demoTimerRef = useRef<any>(null);

  const [currentAd, setCurrentAd] = useState(0);
  const [distance, setDistance] = useState("--");
  const [eta, setEta] = useState("--");
  const [arrived, setArrived] = useState(false);
  const [driverLabel, setDriverLabel] = useState(
    vehicleReg ? `${driverName} (${vehicleReg})` : driverName
  );
  const [toast, setToast] = useState<{ msg: string; error?: boolean } | null>(null);
  const [isFullscreen, setIsFullscreen] = useState(false);

  useEffect(() => {
    const onChange = () => setIsFullscreen(!!document.fullscreenElement);
    document.addEventListener("fullscreenchange", onChange);

    // Auto-enter fullscreen on first user interaction
    const autoFs = () => {
      if (!document.fullscreenElement) {
        document.documentElement.requestFullscreen?.().catch(() => {});
      }
      document.removeEventListener("click", autoFs);
      document.removeEventListener("touchstart", autoFs);
    };
    document.addEventListener("click", autoFs, { once: true });
    document.addEventListener("touchstart", autoFs, { once: true });

    return () => {
      document.removeEventListener("fullscreenchange", onChange);
      document.removeEventListener("click", autoFs);
      document.removeEventListener("touchstart", autoFs);
    };
  }, []);

  const toggleFullscreen = () => {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen?.().catch(() => {});
    } else {
      document.exitFullscreen?.().catch(() => {});
    }
  };

  // TTS state
  const tts = useRef({ twoMin: false, nearby: false, outside: false, timer: null as any });

  const showToast = useCallback((msg: string, error = false) => {
    setToast({ msg, error });
    setTimeout(() => setToast(null), 3000);
  }, []);

  /* ── TTS distance announcements ────────────────────────────── */
  const handleTts = useCallback(
    (distMeters: number) => {
      const name = driverName || "your driver";
      const reg = vehicleReg || "your taxi";

      if (!tts.current.twoMin && distMeters <= 600 && distMeters > 250) {
        tts.current.twoMin = true;
        speak(
          `Your driver, ${name}, in vehicle ${reg}, is approximately two minutes away.`
        );
      }
      if (!tts.current.nearby && distMeters <= 250 && distMeters > 60) {
        tts.current.nearby = true;
        speak(`Your driver in vehicle ${reg} is arriving soon.`);
        if (!tts.current.timer) {
          tts.current.timer = setInterval(() => {
            speak(`Your driver in vehicle ${reg} is arriving shortly.`, false);
          }, 30000);
        }
      }
      if (!tts.current.outside && distMeters <= 60) {
        tts.current.outside = true;
        if (tts.current.timer) {
          clearInterval(tts.current.timer);
          tts.current.timer = null;
        }
        speak(
          `Your ride is here. Your driver in vehicle ${reg} has arrived. Please enjoy your journey.`
        );
      }
    },
    [driverName, vehicleReg]
  );

  /* ── OSRM route update ─────────────────────────────────────── */
  const updateRoute = useCallback(
    async (driverCoords: [number, number]) => {
      if (!leafletMap.current) return;
      const L = (window as any).L;
      const url = `${OSRM_BASE}/${driverCoords[1]},${driverCoords[0]};${pickupLon},${pickupLat}?overview=full&geometries=geojson`;

      try {
        const res = await fetch(url);
        const data = await res.json();
        if (!data.routes?.length) return;

        const route = data.routes[0];
        const coords = route.geometry.coordinates.map(([lng, lat]: number[]) => [lat, lng]);
        const dist = route.distance as number;
        const dur = route.duration as number;

        if (!routeLineRef.current) {
          routeLineRef.current = L.polyline(coords, {
            color: "#3b82f6",
            weight: 4,
            opacity: 0.8,
          }).addTo(leafletMap.current);
        } else {
          routeLineRef.current.setLatLngs(coords);
        }

        setDistance(dist < 1000 ? `${dist.toFixed(0)} m` : `${(dist / 1000).toFixed(2)} km`);
        const etaMin = Math.round(dur / 60);
        setEta(etaMin <= 1 ? "Arriving!" : `${etaMin} min`);

        const bounds = L.latLngBounds([driverCoords, [pickupLat, pickupLon]]);
        leafletMap.current.fitBounds(bounds, { padding: [50, 50] });

        handleTts(dist);
      } catch (err) {
        console.error("Routing error:", err);
      }
    },
    [pickupLat, pickupLon, handleTts]
  );

  /* ── update driver marker ───────────────────────────────────── */
  const updateDriverPosition = useCallback(
    (pos: [number, number]) => {
      const L = (window as any).L;
      if (!leafletMap.current || !L) return;

      const taxiIcon = L.icon({
        iconUrl: "https://cdn-icons-png.flaticon.com/512/3097/3097180.png",
        iconSize: [48, 48],
        iconAnchor: [24, 24],
        popupAnchor: [0, -24],
      });

      if (!driverMarkerRef.current) {
        driverMarkerRef.current = L.marker(pos, { icon: taxiIcon, zIndexOffset: 1000 })
          .addTo(leafletMap.current)
          .bindPopup("🚕 Your Driver");
      } else {
        driverMarkerRef.current.setLatLng(pos);
      }
      updateRoute(pos);
    },
    [updateRoute]
  );

  /* ── init Leaflet (loaded via CDN to match original) ────────── */
  useEffect(() => {
    // Load Leaflet CSS + JS from CDN
    if (!document.getElementById("leaflet-css")) {
      const link = document.createElement("link");
      link.id = "leaflet-css";
      link.rel = "stylesheet";
      link.href = "https://unpkg.com/leaflet@1.9.4/dist/leaflet.css";
      document.head.appendChild(link);
    }

    const loadLeaflet = (): Promise<void> => {
      if ((window as any).L) return Promise.resolve();
      return new Promise((resolve) => {
        if (document.getElementById("leaflet-js")) {
          const check = setInterval(() => {
            if ((window as any).L) { clearInterval(check); resolve(); }
          }, 50);
          return;
        }
        const s = document.createElement("script");
        s.id = "leaflet-js";
        s.src = "https://unpkg.com/leaflet@1.9.4/dist/leaflet.js";
        s.onload = () => resolve();
        document.body.appendChild(s);
      });
    };

    loadLeaflet().then(() => {
      const L = (window as any).L;
      if (!mapRef.current || leafletMap.current) return;

      const map = L.map(mapRef.current, { zoomControl: true, attributionControl: true }).setView(
        [pickupLat, pickupLon],
        14
      );
      L.tileLayer("https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", {
        maxZoom: 19,
        attribution: "© OpenStreetMap",
      }).addTo(map);

      const passengerIcon = L.icon({
        iconUrl: "https://cdn-icons-png.flaticon.com/512/1077/1077012.png",
        iconSize: [40, 40],
        iconAnchor: [20, 40],
        popupAnchor: [0, -35],
      });

      passengerMarkerRef.current = L.marker([pickupLat, pickupLon], { icon: passengerIcon })
        .addTo(map)
        .bindPopup(`📍 Pickup: ${pickupAddr}`)
        .openPopup();

      leafletMap.current = map;
      setTimeout(() => map.invalidateSize(), 150);

      // Show initial driver position
      if (isFinite(initDriverLat) && isFinite(initDriverLon)) {
        updateDriverPosition([initDriverLat, initDriverLon]);
      }
    });

    return () => {
      if (leafletMap.current) {
        leafletMap.current.remove();
        leafletMap.current = null;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /* ── MQTT ───────────────────────────────────────────────────── */
  useEffect(() => {
    const client = mqtt.connect(MQTT_BROKER, {
      clientId: `tracker_${Math.random().toString(16).substr(2, 8)}`,
      clean: true,
    });
    mqttRef.current = client;

    const locationTopic = `drivers/${driverId}/location`;
    const replyTopic = `passengers/${jobId}/replies`;

    client.on("connect", () => {
      client.subscribe(locationTopic);
      if (jobId) client.subscribe(replyTopic);
    });

    client.on("message", (topic: string, message: Buffer) => {
      try {
        const data = JSON.parse(message.toString());
        if (topic === locationTopic) {
          const lat = parseFloat(data.lat ?? data.latitude);
          const lng = parseFloat(data.lng ?? data.longitude);
          if (!isFinite(lat) || !isFinite(lng)) return;

          // Update label from MQTT status if available
          if (typeof data.status === "string") {
            const parts = data.status.split(",");
            if (parts.length >= 2) {
              setDriverLabel(`${parts[0].trim()} (${parts[1].trim()})`);
            }
          }

          lastMqttTs.current = Date.now();
          updateDriverPosition([lat, lng]);
        }
        if (topic === replyTopic) {
          const reply = data.replyType || "";
          if (reply === "on_my_way") showToast("🚕 Driver: On the way!");
          else if (reply === "arrived") showToast("✅ Driver: Arrived at pickup!");
          else if (reply === "custom" && data.text) showToast(`💬 Driver: ${data.text}`);
          else showToast("Driver sent a message.");
        }
      } catch (e) {
        console.error("MQTT parse error:", e);
      }
    });

    return () => { client.end(true); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [driverId, jobId]);

  /* ── ad rotation ────────────────────────────────────────────── */
  useEffect(() => {
    const timer = setInterval(() => {
      setCurrentAd((prev) => (prev + 1) % AD_SLIDES.length);
    }, 4000);
    return () => clearInterval(timer);
  }, []);

  /* ── Demo simulation: fetch OSRM route then animate along it ── */
  useEffect(() => {
    // Fetch the full road route from initial driver pos to pickup
    const fetchDemoRoute = async () => {
      try {
        const url = `${OSRM_BASE}/${initDriverLon},${initDriverLat};${pickupLon},${pickupLat}?overview=full&geometries=geojson&steps=false`;
        const res = await fetch(url);
        const data = await res.json();
        if (!data.routes?.length) return;
        const coords: [number, number][] = data.routes[0].geometry.coordinates.map(
          ([lng, lat]: number[]) => [lat, lng] as [number, number]
        );
        demoRouteRef.current = coords;
        demoIndexRef.current = 0;

        // Start animating along the route
        demoTimerRef.current = setInterval(() => {
          // If real MQTT data is flowing, pause demo
          if (Date.now() - lastMqttTs.current < 15000) return;

          const route = demoRouteRef.current;
          if (!route) return;
          const idx = demoIndexRef.current;
          if (idx >= route.length) {
            clearInterval(demoTimerRef.current);
            setArrived(true);
            return;
          }

          // Move 1-3 points per tick for smooth but visible movement
          const step = Math.min(3, route.length - idx);
          const nextIdx = idx + step;
          demoIndexRef.current = nextIdx;
          const pos = route[Math.min(nextIdx, route.length - 1)];
          updateDriverPosition(pos);

          // Check arrival
          const dist = haversine(pos[0], pos[1], pickupLat, pickupLon);
          if (dist < 30) {
            clearInterval(demoTimerRef.current);
            setArrived(true);
          }
        }, 2000);
      } catch (err) {
        console.error("Demo route fetch failed:", err);
      }
    };

    // Delay slightly so map loads first
    const timeout = setTimeout(fetchDemoRoute, 2000);
    return () => {
      clearTimeout(timeout);
      if (demoTimerRef.current) clearInterval(demoTimerRef.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /* ── passenger GPS ──────────────────────────────────────────── */
  useEffect(() => {
    if (!navigator.geolocation) return;
    const id = navigator.geolocation.watchPosition(
      (pos) => {
        console.log("📍 Passenger GPS:", pos.coords.latitude, pos.coords.longitude);
      },
      () => {},
      { enableHighAccuracy: true, maximumAge: 2000, timeout: 5000 }
    );
    return () => navigator.geolocation.clearWatch(id);
  }, []);

  /* ── preset buttons ─────────────────────────────────────────── */
  const sendPreset = (type: string) => {
    if (!mqttRef.current) return;
    const payload = {
      type: "passenger_preset",
      presetType: type,
      driverId,
      jobId,
      timestamp: new Date().toISOString(),
      passengerLocation: [pickupLat, pickupLon],
    };
    mqttRef.current.publish(`drivers/${driverId}/presets`, JSON.stringify(payload));
    const messages: Record<string, string> = {
      where_are_you: "Asked driver for location update",
      cancel_ride: "Ride cancellation sent",
      call_driver: "Call request sent to driver",
    };
    showToast(messages[type] || "Request sent");
  };

  return (
    <>
      <style>{`
        :root {
          --safe-area-inset-top: env(safe-area-inset-top, 0px);
          --safe-area-inset-bottom: env(safe-area-inset-bottom, 0px);
          --safe-area-inset-left: env(safe-area-inset-left, 0px);
          --safe-area-inset-right: env(safe-area-inset-right, 0px);
        }
        .track-app {
          display: flex; flex-direction: column;
          height: 100vh; height: calc(100vh + var(--safe-area-inset-top));
          width: 100vw; position: fixed; top:0; left:0; right:0; bottom:0;
          overflow: hidden; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif;
        }
        .track-ad-banner {
          position: relative;
          height: calc(70px + var(--safe-area-inset-top));
          padding-top: var(--safe-area-inset-top);
          overflow: hidden;
          background: linear-gradient(135deg, #2d3748 0%, #1a202c 100%);
          flex-shrink: 0;
        }
        .track-ad-slide {
          position: absolute; top: var(--safe-area-inset-top);
          left: var(--safe-area-inset-left); right: var(--safe-area-inset-right); bottom: 0;
          display: flex; justify-content: center; align-items: center;
          opacity: 0; transition: opacity 1s ease-in-out;
        }
        .track-ad-slide.active { opacity: 1; }
        .track-ad-slide img { width: 100%; height: 100%; object-fit: cover; }
        .track-info-bar {
          background: linear-gradient(180deg, #2d3748 0%, #1a202c 100%);
          color: #fff; padding: 16px; flex-shrink: 0;
          box-shadow: 0 2px 8px rgba(0,0,0,0.15);
        }
        .track-info-container {
          display: flex; justify-content: space-around; align-items: center; gap: 16px;
          max-width: 1200px; margin: 0 auto;
        }
        .track-info-item { display: flex; flex-direction: column; align-items: center; flex: 1; min-width: 0; }
        .track-info-label {
          font-size: 12px; font-weight: 600; color: #fbbf24;
          margin-bottom: 4px; text-transform: uppercase; letter-spacing: 0.5px;
        }
        .track-info-value {
          font-size: 16px; font-weight: 600; color: #fff;
          white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 100%;
        }
        .track-info-divider { width: 1px; height: 32px; background: rgba(255,255,255,0.2); }
        .track-map { flex: 1; width: 100%; position: relative; min-height: 0; overflow: hidden; }
        .track-map .leaflet-container { height: 100%; width: 100%; }
        .track-controls {
          background: #1a202c; padding: 12px 16px;
          padding-bottom: calc(80px + var(--safe-area-inset-bottom));
          box-shadow: 0 -4px 16px rgba(0,0,0,0.12);
          border-top: 1px solid #fbbf24; flex-shrink: 0;
        }
        .track-btn-container {
          display: flex; gap: 8px; justify-content: center; max-width: 1200px; margin: 0 auto;
        }
        .track-btn {
          flex: 1 1 0; min-width: 0; padding: 12px 8px; border-radius: 12px;
          font-size: 14px; font-weight: 600; border: none; cursor: pointer;
          display: flex; align-items: center; justify-content: center; gap: 6px;
          transition: all 0.3s cubic-bezier(0.4,0,0.2,1);
          box-shadow: 0 2px 8px rgba(0,0,0,0.1); white-space: nowrap;
        }
        .track-btn:active { transform: scale(0.95); }
        .track-btn-primary { background: #fbbf24; color: #1a202c; }
        .track-btn-danger { background: #ef4444; color: #fff; }
        .track-btn-secondary { background: #3b82f6; color: #fff; }
        .track-toast {
          position: fixed; bottom: calc(140px + var(--safe-area-inset-bottom));
          left: 50%; transform: translateX(-50%);
          background: #1a202c; color: #fff; padding: 12px 24px;
          border-radius: 8px; box-shadow: 0 4px 16px rgba(0,0,0,0.2);
          z-index: 1000; opacity: 0; transition: opacity 0.3s ease; pointer-events: none;
        }
        .track-toast.show { opacity: 1; }
        .track-toast.error { background: #ef4444; }
        .track-arrived-overlay {
          position: absolute; top: 0; left: 0; right: 0; bottom: 0;
          background: rgba(0,0,0,0.6); display: flex; flex-direction: column;
          align-items: center; justify-content: center; z-index: 999;
          animation: fadeInOverlay 0.5s ease-out;
        }
        @keyframes fadeInOverlay {
          from { opacity: 0; } to { opacity: 1; }
        }
        @keyframes bounceIn {
          0% { transform: scale(0.3); opacity: 0; }
          50% { transform: scale(1.05); }
          70% { transform: scale(0.95); }
          100% { transform: scale(1); opacity: 1; }
        }
        .track-arrived-card {
          background: linear-gradient(135deg, #16a34a 0%, #15803d 100%);
          color: white; border-radius: 20px; padding: 32px 40px;
          text-align: center; box-shadow: 0 8px 32px rgba(0,0,0,0.3);
          animation: bounceIn 0.6s ease-out;
        }
        .track-arrived-card .emoji { font-size: 48px; margin-bottom: 12px; }
        .track-arrived-card h2 { font-size: 24px; font-weight: 700; margin-bottom: 8px; }
        .track-arrived-card p { font-size: 16px; opacity: 0.9; }
        .track-arrived-card .reg { 
          display: inline-block; margin-top: 12px; padding: 6px 16px;
          background: rgba(255,255,255,0.2); border-radius: 8px;
          font-size: 18px; font-weight: 700; letter-spacing: 2px;
        }
        @keyframes pulseGlow {
          0%, 100% { box-shadow: 0 0 8px rgba(251,191,36,0.4); }
          50% { box-shadow: 0 0 20px rgba(251,191,36,0.8); }
        }
        .track-info-bar.arriving { animation: pulseGlow 1.5s ease-in-out infinite; }
        .track-fullscreen-btn {
          position: absolute; top: calc(8px + var(--safe-area-inset-top)); right: 8px;
          z-index: 1001; width: 40px; height: 40px; border-radius: 8px;
          background: rgba(26,32,44,0.8); border: 1px solid rgba(251,191,36,0.4);
          color: #fbbf24; font-size: 20px; cursor: pointer;
          display: flex; align-items: center; justify-content: center;
          backdrop-filter: blur(4px); transition: all 0.2s;
        }
        .track-fullscreen-btn:active { transform: scale(0.9); }
        @media (max-width: 768px) {
          .track-ad-banner { height: calc(60px + var(--safe-area-inset-top)); }
          .track-info-value { font-size: 14px; }
          .track-info-label { font-size: 11px; }
          .track-controls { padding-bottom: calc(70px + var(--safe-area-inset-bottom)); }
          .track-btn { padding: 10px 6px; font-size: 12px; min-height: 48px; gap: 4px; }
          .track-arrived-card { padding: 24px 28px; }
          .track-arrived-card .emoji { font-size: 40px; }
          .track-arrived-card h2 { font-size: 20px; }
        }
      `}</style>

      <div className="track-app">
        {/* Fullscreen toggle */}
        <button className="track-fullscreen-btn" onClick={toggleFullscreen} title={isFullscreen ? "Exit fullscreen" : "Go fullscreen"}>
          {isFullscreen ? "✕" : "⛶"}
        </button>

        {/* Ad Banner */}
        <div className="track-ad-banner">
          {AD_SLIDES.map((url, i) => (
            <div key={i} className={`track-ad-slide ${i === currentAd ? "active" : ""}`}>
              <img src={url} alt={`Ad ${i + 1}`} />
            </div>
          ))}
        </div>

        {/* Info Bar */}
        <div className={`track-info-bar ${eta === "Arriving!" ? "arriving" : ""}`}>
          <div className="track-info-container">
            <div className="track-info-item">
              <span className="track-info-label">🚖 Driver</span>
              <span className="track-info-value">{driverLabel}</span>
            </div>
            <div className="track-info-divider" />
            <div className="track-info-item">
              <span className="track-info-label">📍 Distance</span>
              <span className="track-info-value">{arrived ? "Here!" : distance}</span>
            </div>
            <div className="track-info-divider" />
            <div className="track-info-item">
              <span className="track-info-label">⏱ ETA</span>
              <span className="track-info-value">{arrived ? "Arrived!" : eta}</span>
            </div>
          </div>
        </div>

        {/* Map */}
        <div className="track-map">
          <div ref={mapRef} style={{ height: "100%", width: "100%" }} />
          {arrived && (
            <div className="track-arrived-overlay">
              <div className="track-arrived-card">
                <div className="emoji">🚕</div>
                <h2>Your taxi has arrived!</h2>
                <p>Look out for {driverName}</p>
                {vehicleReg && <div className="reg">{vehicleReg}</div>}
              </div>
            </div>
          )}
        </div>

        {/* Controls */}
        <div className="track-controls">
          <div className="track-btn-container">
            <button className="track-btn track-btn-primary" onClick={() => sendPreset("where_are_you")}>
              📍 Where are you?
            </button>
            <button className="track-btn track-btn-danger" onClick={() => sendPreset("cancel_ride")}>
              ❌ Cancel Ride
            </button>
            <button className="track-btn track-btn-secondary" onClick={() => sendPreset("call_driver")}>
              📞 Call Driver
            </button>
          </div>
        </div>

        {/* Toast */}
        <div className={`track-toast ${toast ? "show" : ""} ${toast?.error ? "error" : ""}`}>
          {toast?.msg}
        </div>
      </div>
    </>
  );
}
