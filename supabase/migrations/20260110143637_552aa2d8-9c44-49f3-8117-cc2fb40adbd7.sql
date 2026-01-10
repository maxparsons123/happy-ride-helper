-- Add advanced voice settings to agents table
ALTER TABLE public.agents
ADD COLUMN IF NOT EXISTS vad_threshold DECIMAL(3,2) DEFAULT 0.45,
ADD COLUMN IF NOT EXISTS vad_prefix_padding_ms INTEGER DEFAULT 650,
ADD COLUMN IF NOT EXISTS vad_silence_duration_ms INTEGER DEFAULT 1800,
ADD COLUMN IF NOT EXISTS allow_interruptions BOOLEAN DEFAULT true,
ADD COLUMN IF NOT EXISTS silence_timeout_ms INTEGER DEFAULT 8000,
ADD COLUMN IF NOT EXISTS no_reply_timeout_ms INTEGER DEFAULT 9000,
ADD COLUMN IF NOT EXISTS max_no_reply_reprompts INTEGER DEFAULT 2,
ADD COLUMN IF NOT EXISTS echo_guard_ms INTEGER DEFAULT 100,
ADD COLUMN IF NOT EXISTS goodbye_grace_ms INTEGER DEFAULT 4500;

-- Add comments for documentation
COMMENT ON COLUMN public.agents.vad_threshold IS 'Voice Activity Detection sensitivity (0.0-1.0, lower = more sensitive)';
COMMENT ON COLUMN public.agents.vad_prefix_padding_ms IS 'Audio captured before speech detection (ms)';
COMMENT ON COLUMN public.agents.vad_silence_duration_ms IS 'Silence duration to end speech turn (ms)';
COMMENT ON COLUMN public.agents.allow_interruptions IS 'Allow user to interrupt AI speech (barge-in)';
COMMENT ON COLUMN public.agents.silence_timeout_ms IS 'Timeout after asking "anything else?" (ms)';
COMMENT ON COLUMN public.agents.no_reply_timeout_ms IS 'Time before reprompting silent user (ms)';
COMMENT ON COLUMN public.agents.max_no_reply_reprompts IS 'Max times to reprompt before ending call';
COMMENT ON COLUMN public.agents.echo_guard_ms IS 'Ignore transcripts within this time after AI stops (ms)';
COMMENT ON COLUMN public.agents.goodbye_grace_ms IS 'Wait time for goodbye audio to finish (ms)';