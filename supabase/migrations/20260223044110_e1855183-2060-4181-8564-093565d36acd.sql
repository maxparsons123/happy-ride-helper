ALTER TABLE public.agents ADD COLUMN thinning_alpha real DEFAULT 0.88;

COMMENT ON COLUMN public.agents.thinning_alpha IS 'A-law HPF alpha for voice thinning (0.80=tinny, 0.88=default, 0.95=natural)';
