import { useEffect, useState, useRef } from 'react';
import type { DriverCoords } from './use-driver-state';

export type GpsQuality = 'high' | 'medium' | 'poor' | 'none';

export function useGpsTracking(onUpdate?: (coords: DriverCoords) => void) {
  const [coords, setCoords] = useState<DriverCoords | null>(null);
  const [quality, setQuality] = useState<GpsQuality>('none');
  const [error, setError] = useState<string | null>(null);
  const onUpdateRef = useRef(onUpdate);
  onUpdateRef.current = onUpdate;

  useEffect(() => {
    if (!navigator.geolocation) {
      setError('GPS not available');
      return;
    }

    const watchId = navigator.geolocation.watchPosition(
      (pos) => {
        const c: DriverCoords = {
          lat: pos.coords.latitude, lng: pos.coords.longitude,
          accuracy: pos.coords.accuracy || 50, heading: pos.coords.heading || 0,
          speed: pos.coords.speed || 0,
        };
        setCoords(c);
        setError(null);
        setQuality(c.accuracy <= 20 ? 'high' : c.accuracy <= 50 ? 'medium' : 'poor');
        onUpdateRef.current?.(c);
      },
      (err) => {
        setError(err.code === 1 ? 'Location denied' : err.code === 2 ? 'Location unavailable' : 'GPS timeout');
        setQuality('none');
      },
      { enableHighAccuracy: true, maximumAge: 5000, timeout: 15000 }
    );

    return () => navigator.geolocation.clearWatch(watchId);
  }, []);

  return { coords, quality, error };
}
