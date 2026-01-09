-- Add booking_details JSONB column to bookings table
ALTER TABLE public.bookings 
ADD COLUMN booking_details jsonb DEFAULT '{}'::jsonb;

-- Add comment for documentation
COMMENT ON COLUMN public.bookings.booking_details IS 'Complete booking state as JSON: {reference, pickup: {address, time, verified}, destination: {address, verified}, passengers, vehicle_type, luggage, special_requests, fare, eta, status, history: [{at, action, changes}]}';