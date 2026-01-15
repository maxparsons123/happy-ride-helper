-- Add half_duplex column to sip_trunks table
ALTER TABLE public.sip_trunks 
ADD COLUMN half_duplex boolean NOT NULL DEFAULT false;