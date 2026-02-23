
-- When a booking is cancelled, clear the last_pickup/last_destination on the callers table
-- so they don't bias the next call's address resolution
CREATE OR REPLACE FUNCTION public.clear_caller_history_on_cancel()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path TO 'public'
AS $$
BEGIN
  -- Only fire when status changes TO 'cancelled'
  IF NEW.status = 'cancelled' AND (OLD.status IS DISTINCT FROM 'cancelled') THEN
    UPDATE public.callers
    SET 
      last_pickup = NULL,
      last_destination = NULL,
      updated_at = now()
    WHERE phone_number = NEW.caller_phone;
    
    RAISE LOG 'Cleared last_pickup/last_destination for caller % after booking cancellation', NEW.caller_phone;
  END IF;
  
  RETURN NEW;
END;
$$;

CREATE TRIGGER trg_clear_caller_history_on_cancel
  AFTER UPDATE ON public.bookings
  FOR EACH ROW
  EXECUTE FUNCTION public.clear_caller_history_on_cancel();
