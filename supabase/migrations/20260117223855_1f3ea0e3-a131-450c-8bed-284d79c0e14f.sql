-- Remove the unique constraint on caller_phone - callers can have multiple bookings over time
ALTER TABLE public.bookings DROP CONSTRAINT IF EXISTS bookings_caller_phone_key;