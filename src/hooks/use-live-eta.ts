import { useEffect, useState, useRef } from 'react';

const OSRM_BASE = 'https://router.project-osrm.org/route/v1/driving';
const UPDATE_INTERVAL = 15_000; // 15 seconds

/**
 * Calculates live ETA from driver's current position to a target (pickup) using OSRM.
 * Returns ETA in minutes, or null if unavailable.
 */
export function useLiveEta(
  driverLat: number | null | undefined,
  driverLng: number | null | undefined,
  targetLat: number | null | undefined,
  targetLng: number | null | undefined,
  enabled: boolean = true
): { etaMinutes: number | null; distanceKm: number | null } {
  const [etaMinutes, setEtaMinutes] = useState<number | null>(null);
  const [distanceKm, setDistanceKm] = useState<number | null>(null);
  const requestId = useRef(0);

  useEffect(() => {
    if (!enabled || !driverLat || !driverLng || !targetLat || !targetLng) {
      setEtaMinutes(null);
      setDistanceKm(null);
      return;
    }

    const fetchEta = async () => {
      const id = ++requestId.current;
      try {
        const url = `${OSRM_BASE}/${driverLng},${driverLat};${targetLng},${targetLat}?overview=false`;
        const res = await fetch(url);
        const data = await res.json();
        if (id !== requestId.current) return; // stale
        if (data.routes?.[0]) {
          const durationSec = data.routes[0].duration;
          const distM = data.routes[0].distance;
          setEtaMinutes(Math.max(1, Math.round(durationSec / 60)));
          setDistanceKm(Math.round((distM / 1000) * 10) / 10);
        }
      } catch {
        // silently fail, keep last known ETA
      }
    };

    fetchEta();
    const interval = setInterval(fetchEta, UPDATE_INTERVAL);
    return () => clearInterval(interval);
  }, [enabled, driverLat, driverLng, targetLat, targetLng]);

  return { etaMinutes, distanceKm };
}
