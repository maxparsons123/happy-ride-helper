-- Step 1: Delete older duplicate bookings, keeping only the most recent per caller_phone
DELETE FROM public.bookings
WHERE id NOT IN (
  SELECT DISTINCT ON (caller_phone) id
  FROM public.bookings
  ORDER BY caller_phone, updated_at DESC
);

-- Step 2: Drop existing unique constraint on call_id if it exists
ALTER TABLE public.bookings DROP CONSTRAINT IF EXISTS bookings_call_id_key;

-- Step 3: Add unique constraint on caller_phone
ALTER TABLE public.bookings ADD CONSTRAINT bookings_caller_phone_key UNIQUE (caller_phone);

-- Step 4: Create index for faster lookups
CREATE INDEX IF NOT EXISTS idx_bookings_caller_phone ON public.bookings (caller_phone);