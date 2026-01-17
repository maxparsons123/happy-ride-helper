-- Add preferred_language column to callers table
ALTER TABLE public.callers 
ADD COLUMN preferred_language text DEFAULT NULL;

-- Add comment for documentation
COMMENT ON COLUMN public.callers.preferred_language IS 'User preferred language code (e.g., en-GB, es, fr) - remembered across calls';