-- Add user_phone column to call_logs table
ALTER TABLE public.call_logs 
ADD COLUMN IF NOT EXISTS user_phone TEXT;

-- Add index for phone number lookups
CREATE INDEX IF NOT EXISTS idx_call_logs_user_phone ON public.call_logs(user_phone);