
-- Add pickup/destination coordinates to bookings for map display
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS pickup_lat DOUBLE PRECISION;
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS pickup_lng DOUBLE PRECISION;
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS dest_lat DOUBLE PRECISION;
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS dest_lng DOUBLE PRECISION;

-- Enable realtime for bookings
ALTER TABLE public.bookings REPLICA IDENTITY FULL;
ALTER PUBLICATION supabase_realtime ADD TABLE public.bookings;
