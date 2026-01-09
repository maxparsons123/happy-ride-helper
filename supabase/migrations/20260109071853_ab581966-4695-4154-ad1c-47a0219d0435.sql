-- Add address_aliases JSONB to callers table
-- Stores mappings like {"home": "52A David Road, Coventry", "work": "Coventry Train Station"}
ALTER TABLE public.callers 
ADD COLUMN address_aliases jsonb DEFAULT '{}'::jsonb;

-- Add comment for documentation
COMMENT ON COLUMN public.callers.address_aliases IS 'Customer address aliases: {"home": "52A David Road", "work": "Office Park"}';