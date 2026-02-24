import { useEffect, useState, useRef } from 'react';
import type { JobData } from './use-driver-state';

interface GeocodedCoords {
  pickupLat: number;
  pickupLng: number;
  dropoffLat: number;
  dropoffLng: number;
}

const NOMINATIM = 'https://nominatim.openstreetmap.org/search';
const cache = new Map<string, { lat: number; lng: number }>();

async function geocodeAddress(address: string): Promise<{ lat: number; lng: number } | null> {
  if (!address || address === '—' || address === 'Unknown Location' || address === 'Not specified') return null;

  const cached = cache.get(address);
  if (cached) return cached;

  try {
    // Add UK bias for better results
    const params = new URLSearchParams({
      q: address,
      format: 'json',
      limit: '1',
      countrycodes: 'gb',
    });
    const res = await fetch(`${NOMINATIM}?${params}`, {
      headers: { 'User-Agent': 'BCU-DriverApp/1.0' },
    });
    const data = await res.json();
    if (data?.[0]) {
      const result = { lat: parseFloat(data[0].lat), lng: parseFloat(data[0].lon) };
      cache.set(address, result);
      return result;
    }
  } catch (e) {
    console.warn('Geocode failed for:', address, e);
  }
  return null;
}

/**
 * Re-geocodes the pickup/dropoff addresses from a job using Nominatim,
 * returning more accurate coordinates than the ones from the MQTT payload.
 */
export function useGeocodeJob(job: JobData | null | undefined): GeocodedCoords | null {
  const [result, setResult] = useState<GeocodedCoords | null>(null);
  const lastJobId = useRef<string | null>(null);

  useEffect(() => {
    if (!job || job.jobId === lastJobId.current) return;
    lastJobId.current = job.jobId;

    let cancelled = false;

    (async () => {
      const [pickup, dropoff] = await Promise.all([
        geocodeAddress(job.pickupAddress),
        geocodeAddress(job.dropoff),
      ]);

      if (cancelled) return;

      setResult({
        pickupLat: pickup?.lat ?? job.lat,
        pickupLng: pickup?.lng ?? job.lng,
        dropoffLat: dropoff?.lat ?? job.dropoffLat,
        dropoffLng: dropoff?.lng ?? job.dropoffLng,
      });
    })();

    return () => { cancelled = true; };
  }, [job?.jobId, job?.pickupAddress, job?.dropoff]);

  return result;
}
