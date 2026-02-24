import { useState, useCallback } from 'react';
import { useDriverState, type JobData } from '@/hooks/use-driver-state';
import { useMqttDriver } from '@/hooks/use-mqtt-driver';
import { useGpsTracking } from '@/hooks/use-gps-tracking';
import { useGeocodeJob } from '@/hooks/use-geocode-job';
import { useLiveEta } from '@/hooks/use-live-eta';
import { DriverHeader } from '@/components/driver/DriverHeader';
import { DriverMap } from '@/components/driver/DriverMap';
import { GpsStatusBar } from '@/components/driver/GpsStatusBar';
import { JobPanel } from '@/components/driver/JobPanel';
import { DriverMenu } from '@/components/driver/DriverMenu';
import { toast } from 'sonner';

export default function DriverApp() {
  const driver = useDriverState();
  const [menuOpen, setMenuOpen] = useState(false);
  const [activeJobRequest, setActiveJobRequest] = useState<JobData | null>(null);

  const gps = useGpsTracking(driver.setCoords);
  const geocoded = useGeocodeJob(driver.allocatedJob);
  const pickupLat = geocoded?.pickupLat ?? driver.allocatedJob?.lat;
  const pickupLng = geocoded?.pickupLng ?? driver.allocatedJob?.lng;
  const { etaMinutes, distanceKm } = useLiveEta(
    gps.coords?.lat, gps.coords?.lng,
    pickupLat, pickupLng,
    !!driver.allocatedJob
  );

  const handleJobRequest = useCallback((_topic: string, data: any) => {
    const job = driver.addJob(data);
    if (job) {
      setActiveJobRequest(job);
      toast.info(`New job: ${job.pickupAddress}`, { duration: 5000 });
    }
  }, [driver.addJob]);

  const handleJobResult = useCallback((_topic: string, data: any) => {
    const jobId = data.jobId || data.job;
    if (data.result === 'won') {
      driver.updateJobStatus(jobId, 'allocated');
      driver.setPresence('busy');
      toast.success('🎉 You won the job!');
      setActiveJobRequest(null);
    } else if (data.result === 'lost') {
      driver.updateJobStatus(jobId, 'lost');
      toast.error('Job was allocated to another driver.');
    }
  }, [driver.updateJobStatus, driver.setPresence]);

  const mqtt = useMqttDriver({
    driverId: driver.driverId,
    onJobRequest: handleJobRequest,
    onJobResult: handleJobResult,
  });

  const handleAccept = useCallback((job: JobData) => {
    // Send bid (for bidding mode)
    mqtt.publish(`jobs/${job.jobId}/bids`, {
      driverId: driver.driverId,
      jobId: job.jobId,
      lat: gps.coords?.lat || 52.4068,
      lng: gps.coords?.lng || -1.5197,
      timestamp: Date.now(),
    });
    // Send explicit accept response (for manual dispatch mode)
    mqtt.publish(`jobs/${job.jobId}/response`, {
      driver: driver.driverId,
      driverId: driver.driverId,
      jobId: job.jobId,
      accepted: true,
    });
    driver.updateJobStatus(job.jobId, 'allocated');
    driver.setPresence('busy');
    setActiveJobRequest(null);
    toast.success('Bid submitted! Awaiting allocation...');
  }, [mqtt.publish, driver.driverId, gps.coords, driver.updateJobStatus, driver.setPresence]);

  const handleReject = useCallback((job: JobData) => {
    // Notify dispatcher that job was rejected
    mqtt.publish(`jobs/${job.jobId}/response`, {
      driver: driver.driverId,
      driverId: driver.driverId,
      jobId: job.jobId,
      accepted: false,
    });
    driver.updateJobStatus(job.jobId, 'rejected');
    setActiveJobRequest(null);
  }, [driver.updateJobStatus, driver.driverId, mqtt.publish]);

  return (
    <div className="relative w-full h-screen overflow-hidden bg-gray-100" style={{ touchAction: 'manipulation' }}>
      {/* Map Layer */}
      <DriverMap coords={gps.coords} allocatedJob={driver.allocatedJob} geocodedCoords={geocoded} liveEtaMinutes={etaMinutes} liveDistanceKm={distanceKm} />

      {/* Header */}
      <DriverHeader presence={driver.presence} onMenuToggle={() => setMenuOpen(prev => !prev)} />

      {/* GPS Status */}
      <GpsStatusBar
        coords={gps.coords}
        quality={gps.quality}
        error={gps.error}
        mqttStatus={mqtt.connectionStatus}
      />

      {/* Active Job Request Panel */}
      {activeJobRequest && !menuOpen && (
        <JobPanel
          job={activeJobRequest}
          onAccept={handleAccept}
          onReject={handleReject}
        />
      )}

      {/* Menu Overlay */}
      <DriverMenu
        isOpen={menuOpen}
        onClose={() => setMenuOpen(false)}
        allocatedJob={driver.allocatedJob}
        jobs={driver.jobs}
        presence={driver.presence}
        onPresenceChange={driver.setPresence}
        driverId={driver.driverId}
        onJobStatusChange={driver.updateJobStatus}
      />
    </div>
  );
}
