-- Add audio_source column to distinguish user vs AI audio
ALTER TABLE public.live_call_audio 
ADD COLUMN audio_source TEXT NOT NULL DEFAULT 'ai' CHECK (audio_source IN ('user', 'ai'));

-- Create index for efficient filtering
CREATE INDEX idx_live_call_audio_source ON public.live_call_audio(call_id, audio_source);