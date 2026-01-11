-- Add last_booking_at timestamp to callers table
ALTER TABLE public.callers 
ADD COLUMN last_booking_at timestamp with time zone;