CREATE EXTENSION IF NOT EXISTS "pg_graphql";
CREATE EXTENSION IF NOT EXISTS "pg_stat_statements" WITH SCHEMA "extensions";
CREATE EXTENSION IF NOT EXISTS "pg_trgm" WITH SCHEMA "public";
CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "extensions";
CREATE EXTENSION IF NOT EXISTS "plpgsql";
CREATE EXTENSION IF NOT EXISTS "supabase_vault";
CREATE EXTENSION IF NOT EXISTS "uuid-ossp" WITH SCHEMA "extensions";
BEGIN;

--
-- PostgreSQL database dump
--


-- Dumped from database version 17.6
-- Dumped by pg_dump version 18.1

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: public; Type: SCHEMA; Schema: -; Owner: -
--



--
-- Name: cleanup_old_audio(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.cleanup_old_audio() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
  DELETE FROM public.live_call_audio 
  WHERE created_at < now() - INTERVAL '30 seconds';
  RETURN NEW;
END;
$$;


--
-- Name: update_updated_at_column(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.update_updated_at_column() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path TO 'public'
    AS $$
BEGIN
NEW.updated_at = now();
RETURN NEW;
END;
$$;


SET default_table_access_method = heap;

--
-- Name: address_cache; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.address_cache (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    raw_input text NOT NULL,
    normalized text NOT NULL,
    display_name text NOT NULL,
    city text,
    lat double precision,
    lon double precision,
    use_count integer DEFAULT 1,
    created_at timestamp with time zone DEFAULT now(),
    last_used_at timestamp with time zone DEFAULT now()
);


--
-- Name: bookings; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.bookings (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    call_id text NOT NULL,
    caller_phone text NOT NULL,
    caller_name text,
    pickup text NOT NULL,
    destination text NOT NULL,
    passengers integer DEFAULT 1 NOT NULL,
    fare text,
    eta text,
    status text DEFAULT 'active'::text NOT NULL,
    booked_at timestamp with time zone DEFAULT now() NOT NULL,
    scheduled_for timestamp with time zone,
    cancelled_at timestamp with time zone,
    completed_at timestamp with time zone,
    cancellation_reason text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    booking_details jsonb DEFAULT '{}'::jsonb
);


--
-- Name: call_logs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.call_logs (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    call_id text NOT NULL,
    user_transcript text,
    ai_response text,
    pickup text,
    destination text,
    passengers integer,
    estimated_fare text,
    booking_status text DEFAULT 'collecting'::text,
    stt_latency_ms integer,
    ai_latency_ms integer,
    tts_latency_ms integer,
    total_latency_ms integer,
    call_start_at timestamp with time zone,
    call_end_at timestamp with time zone,
    turn_number integer DEFAULT 1,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    user_phone text
);


--
-- Name: callers; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.callers (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    phone_number text NOT NULL,
    name text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    total_bookings integer DEFAULT 0 NOT NULL,
    last_pickup text,
    last_destination text,
    trusted_addresses text[] DEFAULT '{}'::text[],
    known_areas jsonb DEFAULT '{}'::jsonb,
    address_aliases jsonb DEFAULT '{}'::jsonb
);


--
-- Name: live_call_audio; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.live_call_audio (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    call_id text NOT NULL,
    audio_chunk text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE ONLY public.live_call_audio REPLICA IDENTITY FULL;


--
-- Name: live_calls; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.live_calls (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    call_id text NOT NULL,
    source text DEFAULT 'web'::text NOT NULL,
    status text DEFAULT 'active'::text NOT NULL,
    pickup text,
    destination text,
    passengers integer,
    transcripts jsonb DEFAULT '[]'::jsonb NOT NULL,
    booking_confirmed boolean DEFAULT false NOT NULL,
    fare text,
    eta text,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    ended_at timestamp with time zone,
    caller_name text,
    caller_phone text,
    caller_total_bookings integer DEFAULT 0,
    caller_last_pickup text,
    caller_last_destination text
);


--
-- Name: sip_trunks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sip_trunks (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    name text NOT NULL,
    description text,
    sip_server text,
    sip_username text,
    sip_password text,
    webhook_token text DEFAULT encode(extensions.gen_random_bytes(16), 'hex'::text) NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: address_cache address_cache_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.address_cache
    ADD CONSTRAINT address_cache_pkey PRIMARY KEY (id);


--
-- Name: bookings bookings_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bookings
    ADD CONSTRAINT bookings_pkey PRIMARY KEY (id);


--
-- Name: call_logs call_logs_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.call_logs
    ADD CONSTRAINT call_logs_pkey PRIMARY KEY (id);


--
-- Name: callers callers_phone_number_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.callers
    ADD CONSTRAINT callers_phone_number_key UNIQUE (phone_number);


--
-- Name: callers callers_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.callers
    ADD CONSTRAINT callers_pkey PRIMARY KEY (id);


--
-- Name: live_call_audio live_call_audio_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.live_call_audio
    ADD CONSTRAINT live_call_audio_pkey PRIMARY KEY (id);


--
-- Name: live_calls live_calls_call_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.live_calls
    ADD CONSTRAINT live_calls_call_id_key UNIQUE (call_id);


--
-- Name: live_calls live_calls_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.live_calls
    ADD CONSTRAINT live_calls_pkey PRIMARY KEY (id);


--
-- Name: sip_trunks sip_trunks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sip_trunks
    ADD CONSTRAINT sip_trunks_pkey PRIMARY KEY (id);


--
-- Name: idx_address_cache_city; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_address_cache_city ON public.address_cache USING btree (city);


--
-- Name: idx_address_cache_trgm; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_address_cache_trgm ON public.address_cache USING gin (normalized public.gin_trgm_ops);


--
-- Name: idx_address_cache_unique; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX idx_address_cache_unique ON public.address_cache USING btree (normalized, city) WHERE (city IS NOT NULL);


--
-- Name: idx_address_cache_unique_nocity; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX idx_address_cache_unique_nocity ON public.address_cache USING btree (normalized) WHERE (city IS NULL);


--
-- Name: idx_bookings_phone_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_bookings_phone_status ON public.bookings USING btree (caller_phone, status);


--
-- Name: idx_bookings_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_bookings_status ON public.bookings USING btree (status);


--
-- Name: idx_call_logs_booking_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_call_logs_booking_status ON public.call_logs USING btree (booking_status);


--
-- Name: idx_call_logs_call_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_call_logs_call_id ON public.call_logs USING btree (call_id);


--
-- Name: idx_call_logs_created_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_call_logs_created_at ON public.call_logs USING btree (created_at DESC);


--
-- Name: idx_call_logs_user_phone; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_call_logs_user_phone ON public.call_logs USING btree (user_phone);


--
-- Name: idx_live_call_audio_call_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_live_call_audio_call_id ON public.live_call_audio USING btree (call_id);


--
-- Name: idx_live_calls_call_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_live_calls_call_id ON public.live_calls USING btree (call_id);


--
-- Name: idx_live_calls_status; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_live_calls_status ON public.live_calls USING btree (status);


--
-- Name: live_call_audio cleanup_audio_on_insert; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER cleanup_audio_on_insert AFTER INSERT ON public.live_call_audio FOR EACH STATEMENT EXECUTE FUNCTION public.cleanup_old_audio();


--
-- Name: bookings update_bookings_updated_at; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER update_bookings_updated_at BEFORE UPDATE ON public.bookings FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();


--
-- Name: callers update_callers_updated_at; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER update_callers_updated_at BEFORE UPDATE ON public.callers FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();


--
-- Name: live_calls update_live_calls_updated_at; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER update_live_calls_updated_at BEFORE UPDATE ON public.live_calls FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();


--
-- Name: sip_trunks update_sip_trunks_updated_at; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER update_sip_trunks_updated_at BEFORE UPDATE ON public.sip_trunks FOR EACH ROW EXECUTE FUNCTION public.update_updated_at_column();


--
-- Name: sip_trunks Allow all operations on sip_trunks; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Allow all operations on sip_trunks" ON public.sip_trunks USING (true) WITH CHECK (true);


--
-- Name: address_cache Anyone can read address cache; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Anyone can read address cache" ON public.address_cache FOR SELECT USING (true);


--
-- Name: live_call_audio Anyone can read live audio; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Anyone can read live audio" ON public.live_call_audio FOR SELECT USING (true);


--
-- Name: bookings Anyone can view bookings; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Anyone can view bookings" ON public.bookings FOR SELECT USING (true);


--
-- Name: callers Anyone can view callers; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Anyone can view callers" ON public.callers FOR SELECT USING (true);


--
-- Name: live_calls Anyone can view live calls; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Anyone can view live calls" ON public.live_calls FOR SELECT USING (true);


--
-- Name: live_call_audio Service can insert audio; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service can insert audio" ON public.live_call_audio FOR INSERT WITH CHECK (true);


--
-- Name: call_logs Service role can insert call logs; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can insert call logs" ON public.call_logs FOR INSERT WITH CHECK (true);


--
-- Name: address_cache Service role can manage address cache; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can manage address cache" ON public.address_cache USING (true) WITH CHECK (true);


--
-- Name: bookings Service role can manage bookings; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can manage bookings" ON public.bookings USING (true) WITH CHECK (true);


--
-- Name: callers Service role can manage callers; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can manage callers" ON public.callers USING (true) WITH CHECK (true);


--
-- Name: live_calls Service role can manage live calls; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can manage live calls" ON public.live_calls USING (true) WITH CHECK (true);


--
-- Name: call_logs Service role can update call logs; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can update call logs" ON public.call_logs FOR UPDATE USING (true);


--
-- Name: call_logs Service role can view call logs; Type: POLICY; Schema: public; Owner: -
--

CREATE POLICY "Service role can view call logs" ON public.call_logs FOR SELECT USING (true);


--
-- Name: address_cache; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.address_cache ENABLE ROW LEVEL SECURITY;

--
-- Name: bookings; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.bookings ENABLE ROW LEVEL SECURITY;

--
-- Name: call_logs; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.call_logs ENABLE ROW LEVEL SECURITY;

--
-- Name: callers; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.callers ENABLE ROW LEVEL SECURITY;

--
-- Name: live_call_audio; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.live_call_audio ENABLE ROW LEVEL SECURITY;

--
-- Name: live_calls; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.live_calls ENABLE ROW LEVEL SECURITY;

--
-- Name: sip_trunks; Type: ROW SECURITY; Schema: public; Owner: -
--

ALTER TABLE public.sip_trunks ENABLE ROW LEVEL SECURITY;

--
-- PostgreSQL database dump complete
--




COMMIT;