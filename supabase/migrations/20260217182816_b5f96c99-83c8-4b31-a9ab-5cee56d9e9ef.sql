
ALTER TABLE public.companies
  ADD COLUMN IF NOT EXISTS address text,
  ADD COLUMN IF NOT EXISTS contact_name text,
  ADD COLUMN IF NOT EXISTS contact_phone text,
  ADD COLUMN IF NOT EXISTS contact_email text,
  ADD COLUMN IF NOT EXISTS api_key text,
  ADD COLUMN IF NOT EXISTS api_endpoint text,
  ADD COLUMN IF NOT EXISTS opening_hours jsonb DEFAULT '{}'::jsonb;
