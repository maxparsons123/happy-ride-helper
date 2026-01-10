-- Enable realtime for live_calls table
ALTER TABLE public.live_calls REPLICA IDENTITY FULL;
ALTER PUBLICATION supabase_realtime ADD TABLE public.live_calls;

-- Also enable for live_call_audio (used for audio streaming)
ALTER TABLE public.live_call_audio REPLICA IDENTITY FULL;
ALTER PUBLICATION supabase_realtime ADD TABLE public.live_call_audio;