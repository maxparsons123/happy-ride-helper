-- Add caller_last_booking_at to live_calls for dashboard display
ALTER TABLE public.live_calls 
ADD COLUMN caller_last_booking_at timestamp with time zone;