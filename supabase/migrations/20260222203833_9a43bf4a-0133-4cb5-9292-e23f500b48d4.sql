-- Add escalation tracking to live_calls
ALTER TABLE public.live_calls
ADD COLUMN IF NOT EXISTS escalated boolean NOT NULL DEFAULT false,
ADD COLUMN IF NOT EXISTS escalation_reason text,
ADD COLUMN IF NOT EXISTS escalated_at timestamp with time zone;
