-- Add separate columns for pickup and dropoff address history
ALTER TABLE public.callers 
ADD COLUMN IF NOT EXISTS pickup_addresses text[] DEFAULT '{}',
ADD COLUMN IF NOT EXISTS dropoff_addresses text[] DEFAULT '{}';

-- Add comment for clarity
COMMENT ON COLUMN public.callers.pickup_addresses IS 'Array of verified pickup addresses used by this caller';
COMMENT ON COLUMN public.callers.dropoff_addresses IS 'Array of verified dropoff/destination addresses used by this caller';

-- Clean existing caller data so Ada asks for area on next call
UPDATE public.callers SET 
  known_areas = '{}',
  last_pickup = NULL,
  last_destination = NULL,
  trusted_addresses = '{}',
  pickup_addresses = '{}',
  dropoff_addresses = '{}';