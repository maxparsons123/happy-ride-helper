-- Add unique constraint on call_id for bookings table to enable upserts
ALTER TABLE public.bookings ADD CONSTRAINT bookings_call_id_unique UNIQUE (call_id);