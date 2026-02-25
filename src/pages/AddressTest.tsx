import { useState } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Separator } from "@/components/ui/separator";
import { MapPin, Navigation, Phone, Loader2, CheckCircle, AlertTriangle, Clock } from "lucide-react";

interface AddressResult {
  address?: string;
  street_name?: string;
  street_number?: string;
  city?: string;
  postal_code?: string;
  lat?: number;
  lon?: number;
  poi_lat?: number;
  poi_lng?: number;
  is_ambiguous?: boolean;
  alternatives?: string[];
  resolved_area?: string;
  matched_from_history?: boolean;
  match_type?: "poi" | "residential";
}

interface DispatchResponse {
  status: string;
  detected_area?: string;
  region_source?: string;
  clarification_message?: string;
  pickup?: AddressResult;
  dropoff?: AddressResult;
  fare?: {
    fare?: string;
    fare_spoken?: string;
    distance_miles?: number;
    trip_eta?: string;
    trip_eta_minutes?: number;
    driver_eta_minutes?: number;
    busy_level?: string;
  };
  phone_analysis?: {
    detected_country?: string;
    is_mobile?: boolean;
    landline_city?: string;
  };
  matched_zone?: {
    zone_name?: string;
    company_id?: string;
  };
}

export default function AddressTest() {
  const [pickup, setPickup] = useState("");
  const [destination, setDestination] = useState("");
  const [phone, setPhone] = useState("02476123456");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<DispatchResponse | null>(null);
  const [rawJson, setRawJson] = useState("");
  const [latencyMs, setLatencyMs] = useState<number | null>(null);
  const [error, setError] = useState("");

  const handleSubmit = async () => {
    if (!pickup.trim()) return;
    setLoading(true);
    setError("");
    setResult(null);
    setRawJson("");
    const start = performance.now();

    try {
      const { data, error: fnError } = await supabase.functions.invoke("address-dispatch", {
        body: { pickup, destination, phone },
      });

      const elapsed = Math.round(performance.now() - start);
      setLatencyMs(elapsed);

      if (fnError) {
        setError(fnError.message || "Edge function error");
        return;
      }

      setResult(data as DispatchResponse);
      setRawJson(JSON.stringify(data, null, 2));
    } catch (err: any) {
      setError(err.message || "Unknown error");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-background p-4 md:p-8 max-w-6xl mx-auto">
      <h1 className="text-2xl font-bold text-foreground mb-1">🧪 Address Dispatch Tester</h1>
      <p className="text-muted-foreground text-sm mb-6">
        Type a booking and see what addresses are extracted, geocoded, and matched from zone POIs.
      </p>

      {/* Input Form */}
      <Card className="mb-6">
        <CardContent className="pt-6 space-y-4">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div>
              <label className="text-xs font-medium text-muted-foreground mb-1 block">Pickup Address</label>
              <div className="relative">
                <MapPin className="absolute left-3 top-2.5 h-4 w-4 text-green-500" />
                <Input
                  value={pickup}
                  onChange={(e) => setPickup(e.target.value)}
                  placeholder="e.g. 52A Church Road"
                  className="pl-9"
                  onKeyDown={(e) => e.key === "Enter" && handleSubmit()}
                />
              </div>
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground mb-1 block">Destination</label>
              <div className="relative">
                <Navigation className="absolute left-3 top-2.5 h-4 w-4 text-red-500" />
                <Input
                  value={destination}
                  onChange={(e) => setDestination(e.target.value)}
                  placeholder="e.g. Coventry station"
                  className="pl-9"
                  onKeyDown={(e) => e.key === "Enter" && handleSubmit()}
                />
              </div>
            </div>
            <div>
              <label className="text-xs font-medium text-muted-foreground mb-1 block">Phone Number</label>
              <div className="relative">
                <Phone className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                <Input
                  value={phone}
                  onChange={(e) => setPhone(e.target.value)}
                  placeholder="e.g. 02476123456"
                  className="pl-9"
                  onKeyDown={(e) => e.key === "Enter" && handleSubmit()}
                />
              </div>
            </div>
          </div>
          <Button onClick={handleSubmit} disabled={loading || !pickup.trim()} className="w-full md:w-auto">
            {loading ? <><Loader2 className="h-4 w-4 animate-spin mr-2" /> Extracting...</> : "Extract Addresses"}
          </Button>
        </CardContent>
      </Card>

      {error && (
        <Card className="mb-6 border-destructive">
          <CardContent className="pt-6">
            <p className="text-destructive font-mono text-sm">❌ {error}</p>
          </CardContent>
        </Card>
      )}

      {result && (
        <div className="space-y-4">
          {/* Status Bar */}
          <div className="flex flex-wrap items-center gap-3">
            <Badge variant={result.status === "ready" ? "default" : "destructive"} className="text-sm">
              {result.status === "ready" ? <CheckCircle className="h-3 w-3 mr-1" /> : <AlertTriangle className="h-3 w-3 mr-1" />}
              {result.status}
            </Badge>
            {result.detected_area && (
              <Badge variant="secondary">🌍 {result.detected_area}</Badge>
            )}
            {result.region_source && (
              <Badge variant="outline" className="text-xs">{result.region_source}</Badge>
            )}
            {result.phone_analysis && (
              <Badge variant="outline" className="text-xs">
                📱 {result.phone_analysis.detected_country}
                {result.phone_analysis.is_mobile ? " (mobile)" : ""}
                {result.phone_analysis.landline_city ? ` — ${result.phone_analysis.landline_city}` : ""}
              </Badge>
            )}
            {latencyMs != null && (
              <Badge variant="outline" className="text-xs">
                <Clock className="h-3 w-3 mr-1" /> {latencyMs}ms
              </Badge>
            )}
            {result.matched_zone && (
              <Badge variant="secondary" className="text-xs">
                🗺️ Zone: {result.matched_zone.zone_name}
              </Badge>
            )}
          </div>

          {result.clarification_message && (
            <Card className="border-yellow-500/50 bg-yellow-500/5">
              <CardContent className="pt-4">
                <p className="text-yellow-700 dark:text-yellow-300 font-medium">⚠️ {result.clarification_message}</p>
              </CardContent>
            </Card>
          )}

          {/* Address Cards */}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <AddressCard title="Pickup" icon="📍" address={result.pickup} color="green" />
            <AddressCard title="Dropoff" icon="🏁" address={result.dropoff} color="red" />
          </div>

          {/* Fare */}
          {result.fare && (
            <Card>
              <CardHeader className="pb-2">
                <CardTitle className="text-sm">💰 Fare Estimate</CardTitle>
              </CardHeader>
              <CardContent>
                <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
                  <div>
                    <span className="text-muted-foreground">Fare</span>
                    <p className="font-bold text-lg">{result.fare.fare}</p>
                  </div>
                  <div>
                    <span className="text-muted-foreground">Distance</span>
                    <p className="font-semibold">{result.fare.distance_miles?.toFixed(1)} mi</p>
                  </div>
                  <div>
                    <span className="text-muted-foreground">Trip ETA</span>
                    <p className="font-semibold">{result.fare.trip_eta}</p>
                  </div>
                  <div>
                    <span className="text-muted-foreground">Driver ETA</span>
                    <p className="font-semibold">{result.fare.driver_eta_minutes} min</p>
                  </div>
                </div>
              </CardContent>
            </Card>
          )}

          {/* Raw JSON */}
          <details className="group">
            <summary className="cursor-pointer text-sm text-muted-foreground hover:text-foreground transition-colors">
              📋 Raw JSON Response
            </summary>
            <pre className="mt-2 p-4 bg-muted rounded-lg text-xs overflow-auto max-h-[500px] font-mono">
              {rawJson}
            </pre>
          </details>
        </div>
      )}
    </div>
  );
}

function AddressCard({ title, icon, address, color }: { title: string; icon: string; address?: AddressResult; color: string }) {
  if (!address) return (
    <Card className="opacity-50">
      <CardHeader className="pb-2"><CardTitle className="text-sm">{icon} {title}</CardTitle></CardHeader>
      <CardContent><p className="text-muted-foreground text-sm italic">No data</p></CardContent>
    </Card>
  );

  const hasPoiCoords = address.poi_lat != null && address.poi_lng != null;

  return (
    <Card className={address.is_ambiguous ? "border-yellow-500/50" : ""}>
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <CardTitle className="text-sm">{icon} {title}</CardTitle>
          <div className="flex gap-1 flex-wrap">
            {address.match_type && (
              <Badge
                variant={address.match_type === "poi" ? "default" : "outline"}
                className={`text-xs ${address.match_type === "poi" ? "bg-purple-600" : ""}`}
              >
                {address.match_type === "poi" ? "🏢 POI" : "🏠 Residential"}
              </Badge>
            )}
            {address.is_ambiguous && <Badge variant="destructive" className="text-xs">Ambiguous</Badge>}
            {address.resolved_area && <Badge variant="secondary" className="text-xs">📍 {address.resolved_area}</Badge>}
            {address.matched_from_history && <Badge variant="outline" className="text-xs">📖 History</Badge>}
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-2">
        <p className="font-semibold text-foreground">{address.address || "(empty)"}</p>
        
        <Separator />
        
        <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
          <Field label="Street" value={address.street_name} />
          <Field label="Number" value={address.street_number} />
          <Field label="City" value={address.city} />
          <Field label="Postcode" value={address.postal_code} />
        </div>

        <Separator />

        <div className="text-xs space-y-1">
          {address.match_type === "poi" ? (
            <>
              <div className="flex items-center gap-2">
                <span className="text-muted-foreground w-20">Final Coords:</span>
                <span className="font-mono font-bold text-green-600 dark:text-green-400">
                  {address.lat != null ? `${address.lat.toFixed(5)}, ${address.lon?.toFixed(5)}` : "—"}
                </span>
                <Badge className="text-[10px] bg-green-600">zone_pois ✓</Badge>
              </div>
              <p className="text-muted-foreground italic">POI branch — coords from zone_pois used as final (no Nominatim needed)</p>
            </>
          ) : (
            <>
              <div className="flex items-center gap-2">
                <span className="text-muted-foreground w-20">Coords:</span>
                <span className="font-mono">
                  {address.lat != null ? `${address.lat.toFixed(5)}, ${address.lon?.toFixed(5)}` : "—"}
                </span>
                <Badge variant="outline" className="text-[10px]">Gemini</Badge>
              </div>
              {hasPoiCoords && (
                <div className="flex items-center gap-2">
                  <span className="text-muted-foreground w-20">POI Seed:</span>
                  <span className="font-mono text-muted-foreground">
                    {address.poi_lat!.toFixed(5)}, {address.poi_lng!.toFixed(5)}
                  </span>
                  <Badge variant="outline" className="text-[10px]">zone_pois</Badge>
                </div>
              )}
              <p className="text-muted-foreground italic">Residential branch — needs Nominatim for house-level precision</p>
            </>
          )}
        </div>

        {address.alternatives && address.alternatives.length > 0 && (
          <>
            <Separator />
            <div>
              <span className="text-xs text-muted-foreground">Alternatives:</span>
              <div className="flex flex-wrap gap-1 mt-1">
                {address.alternatives.map((alt, i) => (
                  <Badge key={i} variant="outline" className="text-xs">{alt}</Badge>
                ))}
              </div>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}

function Field({ label, value }: { label: string; value?: string }) {
  return (
    <div>
      <span className="text-muted-foreground">{label}: </span>
      <span className="font-mono">{value || "—"}</span>
    </div>
  );
}
