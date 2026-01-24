-- Add booking_step and last_question_type columns for better session restoration
ALTER TABLE public.live_calls 
ADD COLUMN IF NOT EXISTS booking_step TEXT DEFAULT 'pickup',
ADD COLUMN IF NOT EXISTS last_question_type TEXT,
ADD COLUMN IF NOT EXISTS pickup_time TEXT;