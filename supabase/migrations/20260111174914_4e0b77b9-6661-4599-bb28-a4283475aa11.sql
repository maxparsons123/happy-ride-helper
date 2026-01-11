-- Add clarification_attempts column to track how many times Ada asks questions
ALTER TABLE public.live_calls
ADD COLUMN IF NOT EXISTS clarification_attempts jsonb DEFAULT '{"pickup": 0, "destination": 0, "passengers": 0, "luggage": 0}'::jsonb;