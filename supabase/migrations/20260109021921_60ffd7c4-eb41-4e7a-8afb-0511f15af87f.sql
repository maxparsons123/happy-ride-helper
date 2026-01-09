-- Add trusted_addresses column to callers table
-- Stores an array of addresses the caller has successfully used before
ALTER TABLE public.callers
ADD COLUMN trusted_addresses text[] DEFAULT '{}'::text[];

-- Add a comment explaining the column
COMMENT ON COLUMN public.callers.trusted_addresses IS 'Array of verified addresses this caller has used successfully. Used for auto-verification on repeat bookings.';