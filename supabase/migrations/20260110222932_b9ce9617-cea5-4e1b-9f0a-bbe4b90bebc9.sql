-- Add place name columns to bookings for friendly display (e.g., "Birmingham Airport")
ALTER TABLE public.bookings 
ADD COLUMN pickup_name text,
ADD COLUMN destination_name text;

-- Add comment explaining the columns
COMMENT ON COLUMN public.bookings.pickup_name IS 'Business/place name from Google Places (e.g., Birmingham Airport)';
COMMENT ON COLUMN public.bookings.destination_name IS 'Business/place name from Google Places (e.g., Sweet Spot Cafe)';