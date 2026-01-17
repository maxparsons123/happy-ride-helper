-- Add goodbye grace period column to agents table
-- This controls how long to wait after "Is there anything else?" before accepting soft goodbyes
ALTER TABLE public.agents 
ADD COLUMN IF NOT EXISTS goodbye_grace_ms integer DEFAULT 3000;

-- Add comment for documentation
COMMENT ON COLUMN public.agents.goodbye_grace_ms IS 'Milliseconds to wait after asking "Is there anything else?" before accepting soft goodbye phrases like "no thanks". Default 3000ms (3 seconds).';