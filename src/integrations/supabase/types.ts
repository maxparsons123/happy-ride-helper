export type Json =
  | string
  | number
  | boolean
  | null
  | { [key: string]: Json | undefined }
  | Json[]

export type Database = {
  // Allows to automatically instantiate createClient with right options
  // instead of createClient<Database, { PostgrestVersion: 'XX' }>(URL, KEY)
  __InternalSupabase: {
    PostgrestVersion: "14.1"
  }
  public: {
    Tables: {
      address_cache: {
        Row: {
          city: string | null
          created_at: string | null
          display_name: string
          id: string
          last_used_at: string | null
          lat: number | null
          lon: number | null
          normalized: string
          raw_input: string
          use_count: number | null
        }
        Insert: {
          city?: string | null
          created_at?: string | null
          display_name: string
          id?: string
          last_used_at?: string | null
          lat?: number | null
          lon?: number | null
          normalized: string
          raw_input: string
          use_count?: number | null
        }
        Update: {
          city?: string | null
          created_at?: string | null
          display_name?: string
          id?: string
          last_used_at?: string | null
          lat?: number | null
          lon?: number | null
          normalized?: string
          raw_input?: string
          use_count?: number | null
        }
        Relationships: []
      }
      agents: {
        Row: {
          allow_interruptions: boolean | null
          company_name: string
          created_at: string
          description: string | null
          echo_guard_ms: number | null
          goodbye_grace_ms: number | null
          greeting_style: string | null
          id: string
          is_active: boolean | null
          language: string | null
          max_no_reply_reprompts: number | null
          name: string
          no_reply_timeout_ms: number | null
          personality_traits: Json | null
          silence_timeout_ms: number | null
          slug: string
          system_prompt: string
          thinning_alpha: number | null
          updated_at: string
          use_simple_mode: boolean
          vad_prefix_padding_ms: number | null
          vad_silence_duration_ms: number | null
          vad_threshold: number | null
          voice: string
        }
        Insert: {
          allow_interruptions?: boolean | null
          company_name?: string
          created_at?: string
          description?: string | null
          echo_guard_ms?: number | null
          goodbye_grace_ms?: number | null
          greeting_style?: string | null
          id?: string
          is_active?: boolean | null
          language?: string | null
          max_no_reply_reprompts?: number | null
          name: string
          no_reply_timeout_ms?: number | null
          personality_traits?: Json | null
          silence_timeout_ms?: number | null
          slug: string
          system_prompt: string
          thinning_alpha?: number | null
          updated_at?: string
          use_simple_mode?: boolean
          vad_prefix_padding_ms?: number | null
          vad_silence_duration_ms?: number | null
          vad_threshold?: number | null
          voice?: string
        }
        Update: {
          allow_interruptions?: boolean | null
          company_name?: string
          created_at?: string
          description?: string | null
          echo_guard_ms?: number | null
          goodbye_grace_ms?: number | null
          greeting_style?: string | null
          id?: string
          is_active?: boolean | null
          language?: string | null
          max_no_reply_reprompts?: number | null
          name?: string
          no_reply_timeout_ms?: number | null
          personality_traits?: Json | null
          silence_timeout_ms?: number | null
          slug?: string
          system_prompt?: string
          thinning_alpha?: number | null
          updated_at?: string
          use_simple_mode?: boolean
          vad_prefix_padding_ms?: number | null
          vad_silence_duration_ms?: number | null
          vad_threshold?: number | null
          voice?: string
        }
        Relationships: []
      }
      airport_booking_links: {
        Row: {
          call_id: string | null
          caller_name: string | null
          caller_phone: string | null
          company_id: string | null
          created_at: string
          dest_lat: number | null
          dest_lon: number | null
          destination: string | null
          expires_at: string
          fare_quotes: Json | null
          flight_number: string | null
          id: string
          luggage_hand: number | null
          luggage_suitcases: number | null
          passengers: number | null
          pickup: string | null
          pickup_lat: number | null
          pickup_lon: number | null
          return_datetime: string | null
          return_discount_pct: number | null
          return_flight_number: string | null
          return_trip: boolean | null
          special_instructions: string | null
          status: string
          submitted_at: string | null
          token: string
          travel_datetime: string | null
          updated_at: string
          vehicle_type: string | null
        }
        Insert: {
          call_id?: string | null
          caller_name?: string | null
          caller_phone?: string | null
          company_id?: string | null
          created_at?: string
          dest_lat?: number | null
          dest_lon?: number | null
          destination?: string | null
          expires_at?: string
          fare_quotes?: Json | null
          flight_number?: string | null
          id?: string
          luggage_hand?: number | null
          luggage_suitcases?: number | null
          passengers?: number | null
          pickup?: string | null
          pickup_lat?: number | null
          pickup_lon?: number | null
          return_datetime?: string | null
          return_discount_pct?: number | null
          return_flight_number?: string | null
          return_trip?: boolean | null
          special_instructions?: string | null
          status?: string
          submitted_at?: string | null
          token?: string
          travel_datetime?: string | null
          updated_at?: string
          vehicle_type?: string | null
        }
        Update: {
          call_id?: string | null
          caller_name?: string | null
          caller_phone?: string | null
          company_id?: string | null
          created_at?: string
          dest_lat?: number | null
          dest_lon?: number | null
          destination?: string | null
          expires_at?: string
          fare_quotes?: Json | null
          flight_number?: string | null
          id?: string
          luggage_hand?: number | null
          luggage_suitcases?: number | null
          passengers?: number | null
          pickup?: string | null
          pickup_lat?: number | null
          pickup_lon?: number | null
          return_datetime?: string | null
          return_discount_pct?: number | null
          return_flight_number?: string | null
          return_trip?: boolean | null
          special_instructions?: string | null
          status?: string
          submitted_at?: string | null
          token?: string
          travel_datetime?: string | null
          updated_at?: string
          vehicle_type?: string | null
        }
        Relationships: [
          {
            foreignKeyName: "airport_booking_links_company_id_fkey"
            columns: ["company_id"]
            isOneToOne: false
            referencedRelation: "companies"
            referencedColumns: ["id"]
          },
        ]
      }
      bookings: {
        Row: {
          booked_at: string
          booking_details: Json | null
          call_id: string
          caller_name: string | null
          caller_phone: string
          cancellation_reason: string | null
          cancelled_at: string | null
          company_id: string | null
          completed_at: string | null
          created_at: string
          dest_lat: number | null
          dest_lng: number | null
          destination: string
          destination_name: string | null
          eta: string | null
          fare: string | null
          id: string
          passengers: number
          pickup: string
          pickup_lat: number | null
          pickup_lng: number | null
          pickup_name: string | null
          scheduled_for: string | null
          status: string
          updated_at: string
        }
        Insert: {
          booked_at?: string
          booking_details?: Json | null
          call_id: string
          caller_name?: string | null
          caller_phone: string
          cancellation_reason?: string | null
          cancelled_at?: string | null
          company_id?: string | null
          completed_at?: string | null
          created_at?: string
          dest_lat?: number | null
          dest_lng?: number | null
          destination: string
          destination_name?: string | null
          eta?: string | null
          fare?: string | null
          id?: string
          passengers?: number
          pickup: string
          pickup_lat?: number | null
          pickup_lng?: number | null
          pickup_name?: string | null
          scheduled_for?: string | null
          status?: string
          updated_at?: string
        }
        Update: {
          booked_at?: string
          booking_details?: Json | null
          call_id?: string
          caller_name?: string | null
          caller_phone?: string
          cancellation_reason?: string | null
          cancelled_at?: string | null
          company_id?: string | null
          completed_at?: string | null
          created_at?: string
          dest_lat?: number | null
          dest_lng?: number | null
          destination?: string
          destination_name?: string | null
          eta?: string | null
          fare?: string | null
          id?: string
          passengers?: number
          pickup?: string
          pickup_lat?: number | null
          pickup_lng?: number | null
          pickup_name?: string | null
          scheduled_for?: string | null
          status?: string
          updated_at?: string
        }
        Relationships: [
          {
            foreignKeyName: "bookings_company_id_fkey"
            columns: ["company_id"]
            isOneToOne: false
            referencedRelation: "companies"
            referencedColumns: ["id"]
          },
        ]
      }
      call_logs: {
        Row: {
          ai_latency_ms: number | null
          ai_response: string | null
          booking_status: string | null
          call_end_at: string | null
          call_id: string
          call_start_at: string | null
          created_at: string
          destination: string | null
          estimated_fare: string | null
          id: string
          passengers: number | null
          pickup: string | null
          stt_latency_ms: number | null
          total_latency_ms: number | null
          tts_latency_ms: number | null
          turn_number: number | null
          user_phone: string | null
          user_transcript: string | null
        }
        Insert: {
          ai_latency_ms?: number | null
          ai_response?: string | null
          booking_status?: string | null
          call_end_at?: string | null
          call_id: string
          call_start_at?: string | null
          created_at?: string
          destination?: string | null
          estimated_fare?: string | null
          id?: string
          passengers?: number | null
          pickup?: string | null
          stt_latency_ms?: number | null
          total_latency_ms?: number | null
          tts_latency_ms?: number | null
          turn_number?: number | null
          user_phone?: string | null
          user_transcript?: string | null
        }
        Update: {
          ai_latency_ms?: number | null
          ai_response?: string | null
          booking_status?: string | null
          call_end_at?: string | null
          call_id?: string
          call_start_at?: string | null
          created_at?: string
          destination?: string | null
          estimated_fare?: string | null
          id?: string
          passengers?: number | null
          pickup?: string | null
          stt_latency_ms?: number | null
          total_latency_ms?: number | null
          tts_latency_ms?: number | null
          turn_number?: number | null
          user_phone?: string | null
          user_transcript?: string | null
        }
        Relationships: []
      }
      caller_gps: {
        Row: {
          created_at: string
          expires_at: string
          id: string
          lat: number
          lon: number
          phone_number: string
        }
        Insert: {
          created_at?: string
          expires_at?: string
          id?: string
          lat: number
          lon: number
          phone_number: string
        }
        Update: {
          created_at?: string
          expires_at?: string
          id?: string
          lat?: number
          lon?: number
          phone_number?: string
        }
        Relationships: []
      }
      callers: {
        Row: {
          address_aliases: Json | null
          created_at: string
          dropoff_addresses: string[] | null
          id: string
          known_areas: Json | null
          last_booking_at: string | null
          last_destination: string | null
          last_pickup: string | null
          name: string | null
          phone_number: string
          pickup_addresses: string[] | null
          preferred_language: string | null
          total_bookings: number
          trusted_addresses: string[] | null
          updated_at: string
        }
        Insert: {
          address_aliases?: Json | null
          created_at?: string
          dropoff_addresses?: string[] | null
          id?: string
          known_areas?: Json | null
          last_booking_at?: string | null
          last_destination?: string | null
          last_pickup?: string | null
          name?: string | null
          phone_number: string
          pickup_addresses?: string[] | null
          preferred_language?: string | null
          total_bookings?: number
          trusted_addresses?: string[] | null
          updated_at?: string
        }
        Update: {
          address_aliases?: Json | null
          created_at?: string
          dropoff_addresses?: string[] | null
          id?: string
          known_areas?: Json | null
          last_booking_at?: string | null
          last_destination?: string | null
          last_pickup?: string | null
          name?: string | null
          phone_number?: string
          pickup_addresses?: string[] | null
          preferred_language?: string | null
          total_bookings?: number
          trusted_addresses?: string[] | null
          updated_at?: string
        }
        Relationships: []
      }
      companies: {
        Row: {
          address: string | null
          api_endpoint: string | null
          api_key: string | null
          contact_email: string | null
          contact_name: string | null
          contact_phone: string | null
          created_at: string
          icabbi_app_key: string | null
          icabbi_company_id: string | null
          icabbi_enabled: boolean
          icabbi_secret_key: string | null
          icabbi_site_id: number | null
          icabbi_tenant_base: string | null
          id: string
          is_active: boolean
          name: string
          opening_hours: Json | null
          slug: string
          updated_at: string
          webhook_url: string | null
        }
        Insert: {
          address?: string | null
          api_endpoint?: string | null
          api_key?: string | null
          contact_email?: string | null
          contact_name?: string | null
          contact_phone?: string | null
          created_at?: string
          icabbi_app_key?: string | null
          icabbi_company_id?: string | null
          icabbi_enabled?: boolean
          icabbi_secret_key?: string | null
          icabbi_site_id?: number | null
          icabbi_tenant_base?: string | null
          id?: string
          is_active?: boolean
          name: string
          opening_hours?: Json | null
          slug: string
          updated_at?: string
          webhook_url?: string | null
        }
        Update: {
          address?: string | null
          api_endpoint?: string | null
          api_key?: string | null
          contact_email?: string | null
          contact_name?: string | null
          contact_phone?: string | null
          created_at?: string
          icabbi_app_key?: string | null
          icabbi_company_id?: string | null
          icabbi_enabled?: boolean
          icabbi_secret_key?: string | null
          icabbi_site_id?: number | null
          icabbi_tenant_base?: string | null
          id?: string
          is_active?: boolean
          name?: string
          opening_hours?: Json | null
          slug?: string
          updated_at?: string
          webhook_url?: string | null
        }
        Relationships: []
      }
      dispatch_zones: {
        Row: {
          color_hex: string
          company_id: string | null
          created_at: string
          id: string
          is_active: boolean
          points: Json
          priority: number
          updated_at: string
          zone_name: string
        }
        Insert: {
          color_hex?: string
          company_id?: string | null
          created_at?: string
          id?: string
          is_active?: boolean
          points?: Json
          priority?: number
          updated_at?: string
          zone_name: string
        }
        Update: {
          color_hex?: string
          company_id?: string | null
          created_at?: string
          id?: string
          is_active?: boolean
          points?: Json
          priority?: number
          updated_at?: string
          zone_name?: string
        }
        Relationships: [
          {
            foreignKeyName: "dispatch_zones_company_id_fkey"
            columns: ["company_id"]
            isOneToOne: false
            referencedRelation: "companies"
            referencedColumns: ["id"]
          },
        ]
      }
      live_call_audio: {
        Row: {
          audio_chunk: string
          audio_source: string
          call_id: string
          created_at: string
          id: string
        }
        Insert: {
          audio_chunk: string
          audio_source?: string
          call_id: string
          created_at?: string
          id?: string
        }
        Update: {
          audio_chunk?: string
          audio_source?: string
          call_id?: string
          created_at?: string
          id?: string
        }
        Relationships: []
      }
      live_calls: {
        Row: {
          booking_confirmed: boolean
          booking_step: string | null
          call_id: string
          caller_last_booking_at: string | null
          caller_last_destination: string | null
          caller_last_pickup: string | null
          caller_name: string | null
          caller_phone: string | null
          caller_total_bookings: number | null
          clarification_attempts: Json | null
          company_id: string | null
          confirmation_asked_at: string | null
          destination: string | null
          ended_at: string | null
          escalated: boolean
          escalated_at: string | null
          escalation_reason: string | null
          eta: string | null
          fare: string | null
          gps_lat: number | null
          gps_lon: number | null
          gps_updated_at: string | null
          id: string
          last_question_type: string | null
          passengers: number | null
          pickup: string | null
          pickup_time: string | null
          source: string
          started_at: string
          status: string
          transcripts: Json
          updated_at: string
        }
        Insert: {
          booking_confirmed?: boolean
          booking_step?: string | null
          call_id: string
          caller_last_booking_at?: string | null
          caller_last_destination?: string | null
          caller_last_pickup?: string | null
          caller_name?: string | null
          caller_phone?: string | null
          caller_total_bookings?: number | null
          clarification_attempts?: Json | null
          company_id?: string | null
          confirmation_asked_at?: string | null
          destination?: string | null
          ended_at?: string | null
          escalated?: boolean
          escalated_at?: string | null
          escalation_reason?: string | null
          eta?: string | null
          fare?: string | null
          gps_lat?: number | null
          gps_lon?: number | null
          gps_updated_at?: string | null
          id?: string
          last_question_type?: string | null
          passengers?: number | null
          pickup?: string | null
          pickup_time?: string | null
          source?: string
          started_at?: string
          status?: string
          transcripts?: Json
          updated_at?: string
        }
        Update: {
          booking_confirmed?: boolean
          booking_step?: string | null
          call_id?: string
          caller_last_booking_at?: string | null
          caller_last_destination?: string | null
          caller_last_pickup?: string | null
          caller_name?: string | null
          caller_phone?: string | null
          caller_total_bookings?: number | null
          clarification_attempts?: Json | null
          company_id?: string | null
          confirmation_asked_at?: string | null
          destination?: string | null
          ended_at?: string | null
          escalated?: boolean
          escalated_at?: string | null
          escalation_reason?: string | null
          eta?: string | null
          fare?: string | null
          gps_lat?: number | null
          gps_lon?: number | null
          gps_updated_at?: string | null
          id?: string
          last_question_type?: string | null
          passengers?: number | null
          pickup?: string | null
          pickup_time?: string | null
          source?: string
          started_at?: string
          status?: string
          transcripts?: Json
          updated_at?: string
        }
        Relationships: [
          {
            foreignKeyName: "live_calls_company_id_fkey"
            columns: ["company_id"]
            isOneToOne: false
            referencedRelation: "companies"
            referencedColumns: ["id"]
          },
        ]
      }
      sip_trunks: {
        Row: {
          created_at: string
          description: string | null
          half_duplex: boolean
          id: string
          is_active: boolean
          name: string
          sip_password: string | null
          sip_server: string | null
          sip_username: string | null
          updated_at: string
          webhook_token: string
        }
        Insert: {
          created_at?: string
          description?: string | null
          half_duplex?: boolean
          id?: string
          is_active?: boolean
          name: string
          sip_password?: string | null
          sip_server?: string | null
          sip_username?: string | null
          updated_at?: string
          webhook_token?: string
        }
        Update: {
          created_at?: string
          description?: string | null
          half_duplex?: boolean
          id?: string
          is_active?: boolean
          name?: string
          sip_password?: string | null
          sip_server?: string | null
          sip_username?: string | null
          updated_at?: string
          webhook_token?: string
        }
        Relationships: []
      }
      uk_locations: {
        Row: {
          aliases: string[] | null
          country: string
          created_at: string
          id: string
          is_distinct: boolean | null
          lat: number | null
          lng: number | null
          name: string
          parent_city: string | null
          postcodes: string[] | null
          type: string
        }
        Insert: {
          aliases?: string[] | null
          country?: string
          created_at?: string
          id?: string
          is_distinct?: boolean | null
          lat?: number | null
          lng?: number | null
          name: string
          parent_city?: string | null
          postcodes?: string[] | null
          type?: string
        }
        Update: {
          aliases?: string[] | null
          country?: string
          created_at?: string
          id?: string
          is_distinct?: boolean | null
          lat?: number | null
          lng?: number | null
          name?: string
          parent_city?: string | null
          postcodes?: string[] | null
          type?: string
        }
        Relationships: []
      }
      zone_pois: {
        Row: {
          area: string | null
          created_at: string
          id: string
          lat: number | null
          lng: number | null
          name: string
          osm_id: number | null
          poi_type: string
          zone_id: string
        }
        Insert: {
          area?: string | null
          created_at?: string
          id?: string
          lat?: number | null
          lng?: number | null
          name: string
          osm_id?: number | null
          poi_type?: string
          zone_id: string
        }
        Update: {
          area?: string | null
          created_at?: string
          id?: string
          lat?: number | null
          lng?: number | null
          name?: string
          osm_id?: number | null
          poi_type?: string
          zone_id?: string
        }
        Relationships: [
          {
            foreignKeyName: "zone_pois_zone_id_fkey"
            columns: ["zone_id"]
            isOneToOne: false
            referencedRelation: "dispatch_zones"
            referencedColumns: ["id"]
          },
        ]
      }
    }
    Views: {
      [_ in never]: never
    }
    Functions: {
      find_zone_for_point: {
        Args: { p_lat: number; p_lng: number }
        Returns: {
          company_id: string
          priority: number
          zone_id: string
          zone_name: string
        }[]
      }
      fuzzy_match_street: {
        Args: { p_city: string; p_limit?: number; p_street_name: string }
        Returns: {
          lat: number
          lon: number
          matched_city: string
          matched_name: string
          similarity_score: number
          source: string
        }[]
      }
      fuzzy_match_zone_poi: {
        Args: { p_address: string; p_limit?: number; p_min_similarity?: number }
        Returns: {
          area: string
          company_id: string
          lat: number
          lng: number
          poi_id: string
          poi_name: string
          poi_type: string
          similarity_score: number
          zone_id: string
          zone_name: string
        }[]
      }
      show_limit: { Args: never; Returns: number }
      show_trgm: { Args: { "": string }; Returns: string[] }
      word_fuzzy_match_zone_poi: {
        Args: { p_address: string; p_limit?: number; p_min_similarity?: number }
        Returns: {
          area: string
          company_id: string
          lat: number
          lng: number
          poi_id: string
          poi_name: string
          poi_type: string
          similarity_score: number
          zone_id: string
          zone_name: string
        }[]
      }
    }
    Enums: {
      [_ in never]: never
    }
    CompositeTypes: {
      [_ in never]: never
    }
  }
}

type DatabaseWithoutInternals = Omit<Database, "__InternalSupabase">

type DefaultSchema = DatabaseWithoutInternals[Extract<keyof Database, "public">]

export type Tables<
  DefaultSchemaTableNameOrOptions extends
    | keyof (DefaultSchema["Tables"] & DefaultSchema["Views"])
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof (DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"] &
        DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Views"])
    : never = never,
> = DefaultSchemaTableNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? (DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"] &
      DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Views"])[TableName] extends {
      Row: infer R
    }
    ? R
    : never
  : DefaultSchemaTableNameOrOptions extends keyof (DefaultSchema["Tables"] &
        DefaultSchema["Views"])
    ? (DefaultSchema["Tables"] &
        DefaultSchema["Views"])[DefaultSchemaTableNameOrOptions] extends {
        Row: infer R
      }
      ? R
      : never
    : never

export type TablesInsert<
  DefaultSchemaTableNameOrOptions extends
    | keyof DefaultSchema["Tables"]
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"]
    : never = never,
> = DefaultSchemaTableNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"][TableName] extends {
      Insert: infer I
    }
    ? I
    : never
  : DefaultSchemaTableNameOrOptions extends keyof DefaultSchema["Tables"]
    ? DefaultSchema["Tables"][DefaultSchemaTableNameOrOptions] extends {
        Insert: infer I
      }
      ? I
      : never
    : never

export type TablesUpdate<
  DefaultSchemaTableNameOrOptions extends
    | keyof DefaultSchema["Tables"]
    | { schema: keyof DatabaseWithoutInternals },
  TableName extends DefaultSchemaTableNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"]
    : never = never,
> = DefaultSchemaTableNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[DefaultSchemaTableNameOrOptions["schema"]]["Tables"][TableName] extends {
      Update: infer U
    }
    ? U
    : never
  : DefaultSchemaTableNameOrOptions extends keyof DefaultSchema["Tables"]
    ? DefaultSchema["Tables"][DefaultSchemaTableNameOrOptions] extends {
        Update: infer U
      }
      ? U
      : never
    : never

export type Enums<
  DefaultSchemaEnumNameOrOptions extends
    | keyof DefaultSchema["Enums"]
    | { schema: keyof DatabaseWithoutInternals },
  EnumName extends DefaultSchemaEnumNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[DefaultSchemaEnumNameOrOptions["schema"]]["Enums"]
    : never = never,
> = DefaultSchemaEnumNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[DefaultSchemaEnumNameOrOptions["schema"]]["Enums"][EnumName]
  : DefaultSchemaEnumNameOrOptions extends keyof DefaultSchema["Enums"]
    ? DefaultSchema["Enums"][DefaultSchemaEnumNameOrOptions]
    : never

export type CompositeTypes<
  PublicCompositeTypeNameOrOptions extends
    | keyof DefaultSchema["CompositeTypes"]
    | { schema: keyof DatabaseWithoutInternals },
  CompositeTypeName extends PublicCompositeTypeNameOrOptions extends {
    schema: keyof DatabaseWithoutInternals
  }
    ? keyof DatabaseWithoutInternals[PublicCompositeTypeNameOrOptions["schema"]]["CompositeTypes"]
    : never = never,
> = PublicCompositeTypeNameOrOptions extends {
  schema: keyof DatabaseWithoutInternals
}
  ? DatabaseWithoutInternals[PublicCompositeTypeNameOrOptions["schema"]]["CompositeTypes"][CompositeTypeName]
  : PublicCompositeTypeNameOrOptions extends keyof DefaultSchema["CompositeTypes"]
    ? DefaultSchema["CompositeTypes"][PublicCompositeTypeNameOrOptions]
    : never

export const Constants = {
  public: {
    Enums: {},
  },
} as const
