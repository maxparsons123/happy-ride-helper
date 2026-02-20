
-- Add iCabbi dispatch integration fields to companies table
ALTER TABLE public.companies
  ADD COLUMN IF NOT EXISTS icabbi_enabled boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS icabbi_site_id integer NULL,
  ADD COLUMN IF NOT EXISTS icabbi_company_id text NULL,
  ADD COLUMN IF NOT EXISTS icabbi_app_key text NULL,
  ADD COLUMN IF NOT EXISTS icabbi_secret_key text NULL,
  ADD COLUMN IF NOT EXISTS icabbi_tenant_base text NULL DEFAULT 'https://yourtenant.icabbi.net';
