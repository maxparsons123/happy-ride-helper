
-- Add a bids JSONB column to bookings to track driver bids per job
-- Each entry: { driverId, lat, lng, distanceKm, completedJobs, bidTime }
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS bids jsonb DEFAULT '[]'::jsonb;

-- Add a comment for clarity
COMMENT ON COLUMN public.bookings.bids IS 'Array of driver bid records: [{driverId, lat, lng, distanceKm, completedJobs, bidTime, score}]';
