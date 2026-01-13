-- Add use_simple_mode flag to agents table for dynamic routing
ALTER TABLE public.agents 
ADD COLUMN use_simple_mode boolean NOT NULL DEFAULT false;

-- Add comment for documentation
COMMENT ON COLUMN public.agents.use_simple_mode IS 'When true, routes calls to taxi-realtime-simple instead of taxi-realtime';