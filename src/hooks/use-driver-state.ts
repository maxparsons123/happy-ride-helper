import { useState, useCallback, useRef } from 'react';

export interface JobData {
  jobId: string;
  pickupAddress: string;
  dropoff: string;
  customerName: string;
  customerPhone: string;
  fare: string;
  notes: string;
  passengers: string;
  lat: number;
  lng: number;
  dropoffLat: number;
  dropoffLng: number;
  biddingWindowSec: number;
  status: 'queued' | 'allocated' | 'arrived' | 'completed' | 'rejected' | 'lost';
  timestamp: number;
  raw?: any;
}

export interface DriverCoords {
  lat: number;
  lng: number;
  accuracy: number;
  heading: number;
  speed: number;
}

export type DriverPresence = 'available' | 'busy' | 'offline';

function extractField(obj: any, fieldNames: string[], defaultValue: any = '—'): any {
  for (const name of fieldNames) {
    const value = obj[name];
    if (value !== null && value !== undefined && value !== '') {
      if (typeof value === 'number' && !isNaN(value)) {
        if (['lat', 'lng', 'pickupLat', 'pickupLng', 'dropoffLat', 'dropoffLng', 'originLat', 'originLng', 'startLat', 'startLng', 'destLat', 'destLng', 'destinationLat', 'destinationLng', 'endLat', 'endLng'].includes(name)) {
          if (Math.abs(value) < 0.001) continue;
        }
        return value;
      }
      if (typeof value === 'string' && value.trim() !== '') return value.trim();
    }
  }
  return defaultValue;
}

export function normalizeJobPayload(rawData: any): JobData {
  return {
    jobId: extractField(rawData, ['jobId', 'job', 'bookingRef'], 'UNKNOWN_JOB'),
    pickupAddress: extractField(rawData, ['pickupAddress', 'pickup', 'pubName', 'origin', 'startAddress', 'from'], 'Unknown Location'),
    dropoff: extractField(rawData, ['dropoff', 'dropoffName', 'destination', 'destAddress', 'endAddress', 'to'], 'Not specified'),
    customerName: extractField(rawData, ['customerName', 'callerName', 'passengerName', 'name', 'contactName'], 'Customer'),
    customerPhone: extractField(rawData, ['customerPhone', 'callerPhone', 'phone', 'phoneNumber', 'contactPhone'], '—'),
    fare: extractField(rawData, ['fare', 'estimatedFare', 'estimatedPrice', 'price', 'amount', 'cost'], ''),
    notes: extractField(rawData, ['notes', 'specialRequirements', 'requirements', 'comments', 'instruction', 'remarks'], 'None'),
    passengers: extractField(rawData, ['passengers', 'passengersText', 'pax', 'occupants', 'partySize'], ''),
    lat: extractField(rawData, ['lat', 'pickupLat', 'originLat', 'startLat'], 52.4068),
    lng: extractField(rawData, ['lng', 'pickupLng', 'originLng', 'startLng'], -1.5197),
    dropoffLat: extractField(rawData, ['dropoffLat', 'destinationLat', 'destLat', 'endLat'], 0),
    dropoffLng: extractField(rawData, ['dropoffLng', 'destinationLng', 'destLng', 'endLng'], 0),
    biddingWindowSec: extractField(rawData, ['biddingWindowSec', 'biddingTime', 'window', 'bidTime'], 30),
    status: rawData.status || 'queued',
    timestamp: rawData.timestamp || Date.now(),
    raw: rawData,
  };
}

const STORAGE_PREFIX = 'bcu_driver_';

function getStorageKey(driverId: string, key: string) {
  return `${STORAGE_PREFIX}${driverId}_${key}`;
}

export function useDriverState() {
  const [driverId] = useState(() => {
    const saved = localStorage.getItem('bcu_driver_id');
    if (saved) return saved;
    const id = 'driver_' + Math.random().toString(36).substr(2, 8);
    localStorage.setItem('bcu_driver_id', id);
    return id;
  });

  const [presence, setPresenceState] = useState<DriverPresence>(() => {
    return (localStorage.getItem(getStorageKey(driverId, 'presence')) as DriverPresence) || 'available';
  });

  const [coords, setCoords] = useState<DriverCoords | null>(null);
  const [jobs, setJobsState] = useState<JobData[]>(() => {
    try {
      return JSON.parse(localStorage.getItem(getStorageKey(driverId, 'jobs')) || '[]');
    } catch { return []; }
  });

  const seenJobIds = useRef<Set<string>>(new Set(jobs.map(j => j.jobId)));

  const setPresence = useCallback((p: DriverPresence) => {
    setPresenceState(p);
    localStorage.setItem(getStorageKey(driverId, 'presence'), p);
  }, [driverId]);

  const addJob = useCallback((raw: any): JobData | null => {
    const job = normalizeJobPayload(raw);
    // Deduplicate: if we already have this job as queued, skip re-adding
    const alreadyExists = seenJobIds.current.has(job.jobId);
    seenJobIds.current.add(job.jobId);
    setJobsState(prev => {
      const existing = prev.find(j => j.jobId === job.jobId);
      // If job already exists and is still queued, skip (prevents duplicate toasts)
      if (existing && existing.status === 'queued') return prev;
      const filtered = prev.filter(j => j.jobId !== job.jobId);
      const next = [job, ...filtered].slice(0, 50);
      localStorage.setItem(getStorageKey(driverId, 'jobs'), JSON.stringify(next));
      return next;
    });
    // Only return job (triggering toast) if it's new
    if (alreadyExists) {
      // Check if it was already queued
      const prev = jobs;
      const existing = prev.find(j => j.jobId === job.jobId && j.status === 'queued');
      if (existing) return null;
    }
    return job;
  }, [driverId, jobs]);

  const updateJobStatus = useCallback((jobId: string, status: JobData['status']) => {
    setJobsState(prev => {
      const next = prev.map(j => j.jobId === jobId ? { ...j, status } : j);
      localStorage.setItem(getStorageKey(driverId, 'jobs'), JSON.stringify(next));
      // Persist active booking snapshot when driver arrives
      if (status === 'arrived') {
        const arrivedJob = next.find(j => j.jobId === jobId);
        if (arrivedJob) {
          localStorage.setItem(getStorageKey(driverId, 'active_booking'), JSON.stringify(arrivedJob));
        }
      }
      // Clear active booking when job completes or is rejected
      if (status === 'completed' || status === 'rejected') {
        localStorage.removeItem(getStorageKey(driverId, 'active_booking'));
      }
      return next;
    });
  }, [driverId]);

  const getActiveBooking = useCallback((): JobData | null => {
    try {
      const saved = localStorage.getItem(getStorageKey(driverId, 'active_booking'));
      return saved ? JSON.parse(saved) : null;
    } catch { return null; }
  }, [driverId]);

  const allocatedJob = jobs.find(j => j.status === 'allocated' || j.status === 'arrived');

  return {
    driverId, presence, setPresence, coords, setCoords,
    jobs, addJob, updateJobStatus, allocatedJob, getActiveBooking, seenJobIds,
  };
}
