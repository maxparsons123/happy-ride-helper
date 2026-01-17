/**
 * Supabase Configuration
 * 
 * This file centralizes all Supabase-related configuration.
 * Values are pulled from environment variables (set automatically by Lovable Cloud).
 * 
 * When creating a new instance/remix:
 * 1. The .env file is auto-generated with the new project details
 * 2. All URLs in the app will automatically use the new values
 */

export const SUPABASE_PROJECT_ID = import.meta.env.VITE_SUPABASE_PROJECT_ID || "";
export const SUPABASE_URL = import.meta.env.VITE_SUPABASE_URL || `https://${SUPABASE_PROJECT_ID}.supabase.co`;
export const SUPABASE_ANON_KEY = import.meta.env.VITE_SUPABASE_PUBLISHABLE_KEY || "";

// Edge function URLs
export const getEdgeFunctionUrl = (functionName: string) => 
  `${SUPABASE_URL}/functions/v1/${functionName}`;

export const getEdgeFunctionWsUrl = (functionName: string) => 
  `wss://${SUPABASE_PROJECT_ID}.supabase.co/functions/v1/${functionName}`;

// Commonly used function URLs
export const TAXI_REALTIME_WS_URL = getEdgeFunctionWsUrl("taxi-realtime");
export const TAXI_REALTIME_SIMPLE_WS_URL = getEdgeFunctionWsUrl("taxi-realtime-simple");
export const TAXI_WEBHOOK_TEST_URL = getEdgeFunctionUrl("taxi-webhook-test");

// For external bridges (Python, C#, etc.) - copy these values
export const CONFIG_FOR_BRIDGES = {
  projectId: SUPABASE_PROJECT_ID,
  supabaseUrl: SUPABASE_URL,
  wsUrlRealtimeSimple: TAXI_REALTIME_SIMPLE_WS_URL,
  wsUrlRealtime: TAXI_REALTIME_WS_URL,
  anonKey: SUPABASE_ANON_KEY,
};

// Log config on load (only in development)
if (import.meta.env.DEV) {
  console.log("[Supabase Config]", {
    projectId: SUPABASE_PROJECT_ID,
    url: SUPABASE_URL,
  });
}
