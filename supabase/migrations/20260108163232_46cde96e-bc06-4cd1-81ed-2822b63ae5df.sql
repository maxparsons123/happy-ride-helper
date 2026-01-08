-- Add caller info columns to live_calls table
ALTER TABLE public.live_calls 
ADD COLUMN IF NOT EXISTS caller_name text,
ADD COLUMN IF NOT EXISTS caller_phone text,
ADD COLUMN IF NOT EXISTS caller_total_bookings integer DEFAULT 0,
ADD COLUMN IF NOT EXISTS caller_last_pickup text,
ADD COLUMN IF NOT EXISTS caller_last_destination text;