import { useState, useEffect } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Switch } from "@/components/ui/switch";
import { useToast } from "@/hooks/use-toast";
import { Phone, Plus, Trash2, Copy, Server, Eye, EyeOff, Car, Save, ChevronDown, ChevronUp } from "lucide-react";
import { Link } from "react-router-dom";
import { Badge } from "@/components/ui/badge";

// Credential row component for consistent display
interface CredentialRowProps {
  label: string;
  value: string;
  onCopy: () => void;
  showToggle?: boolean;
  isVisible?: boolean;
  onToggleVisibility?: () => void;
}

const CredentialRow = ({ label, value, onCopy, showToggle, isVisible, onToggleVisibility }: CredentialRowProps) => (
  <div className="flex items-center justify-between py-2 border-b border-border/50 last:border-0">
    <span className="text-sm text-muted-foreground min-w-[180px]">{label}:</span>
    <div className="flex items-center gap-2 flex-1 justify-end">
      <span className="font-mono text-sm text-foreground truncate max-w-[300px]">{value}</span>
      {showToggle && onToggleVisibility && (
        <Button
          variant="ghost"
          size="icon"
          className="h-6 w-6 shrink-0"
          onClick={onToggleVisibility}
        >
          {isVisible ? <EyeOff className="h-3 w-3" /> : <Eye className="h-3 w-3" />}
        </Button>
      )}
      <Button
        variant="ghost"
        size="sm"
        className="h-6 px-2 text-xs text-muted-foreground hover:text-foreground shrink-0"
        onClick={onCopy}
      >
        <Copy className="h-3 w-3 mr-1" />
        Copy
      </Button>
    </div>
  </div>
);

interface SipTrunk {
  id: string;
  name: string;
  description: string | null;
  sip_server: string | null;
  sip_username: string | null;
  sip_password: string | null;
  webhook_token: string;
  is_active: boolean;
  created_at: string;
}

interface IcabbiCompany {
  id: string;
  name: string;
  slug: string;
  is_active: boolean;
  icabbi_enabled: boolean;
  icabbi_site_id: number | null;
  icabbi_company_id: string | null;
  icabbi_app_key: string | null;
  icabbi_secret_key: string | null;
  icabbi_tenant_base: string | null;
}

const SipConfig = () => {
  const [trunks, setTrunks] = useState<SipTrunk[]>([]);
  const [loading, setLoading] = useState(true);
  const [showPasswords, setShowPasswords] = useState<Record<string, boolean>>({});
  const [newTrunk, setNewTrunk] = useState({
    name: "",
    description: "",
    sip_server: "",
    sip_username: "",
    sip_password: "",
  });
  const [showForm, setShowForm] = useState(false);

  // iCabbi state
  const [icabbiCompanies, setIcabbiCompanies] = useState<IcabbiCompany[]>([]);
  const [icabbiLoading, setIcabbiLoading] = useState(true);
  const [icabbiExpanded, setIcabbiExpanded] = useState<Record<string, boolean>>({});
  const [icabbiSecrets, setIcabbiSecrets] = useState<Record<string, boolean>>({});
  const [icabbiSaving, setIcabbiSaving] = useState<string | null>(null);

  const { toast } = useToast();

  const supabaseUrl = import.meta.env.VITE_SUPABASE_URL;

  useEffect(() => {
    fetchTrunks();
    fetchIcabbiCompanies();
  }, []);

  const fetchTrunks = async () => {
    const { data, error } = await supabase
      .from("sip_trunks")
      .select("*")
      .order("created_at", { ascending: false });

    if (error) {
      toast({
        title: "Error fetching SIP trunks",
        description: error.message,
        variant: "destructive",
      });
    } else {
      setTrunks(data || []);
    }
    setLoading(false);
  };

  const fetchIcabbiCompanies = async () => {
    const { data, error } = await supabase
      .from("companies")
      .select("id,name,slug,is_active,icabbi_enabled,icabbi_site_id,icabbi_company_id,icabbi_app_key,icabbi_secret_key,icabbi_tenant_base")
      .order("created_at", { ascending: false });

    if (!error) {
      setIcabbiCompanies((data as IcabbiCompany[]) || []);
    }
    setIcabbiLoading(false);
  };

  const updateIcabbiField = (id: string, field: keyof IcabbiCompany, value: unknown) => {
    setIcabbiCompanies(prev => prev.map(c => c.id === id ? { ...c, [field]: value } : c));
  };

  const saveIcabbiCompany = async (company: IcabbiCompany) => {
    setIcabbiSaving(company.id);
    const { error } = await supabase
      .from("companies")
      .update({
        icabbi_enabled: company.icabbi_enabled,
        icabbi_site_id: company.icabbi_site_id,
        icabbi_company_id: company.icabbi_company_id || null,
        icabbi_app_key: company.icabbi_app_key || null,
        icabbi_secret_key: company.icabbi_secret_key || null,
        icabbi_tenant_base: company.icabbi_tenant_base || "https://yourtenant.icabbi.net",
        updated_at: new Date().toISOString(),
      })
      .eq("id", company.id);

    if (error) {
      toast({ title: "Error saving iCabbi settings", description: error.message, variant: "destructive" });
    } else {
      toast({ title: "iCabbi settings saved", description: `${company.name} updated` });
    }
    setIcabbiSaving(null);
  };

  const createTrunk = async () => {
    if (!newTrunk.name.trim()) {
      toast({
        title: "Name required",
        description: "Please enter a name for the SIP trunk",
        variant: "destructive",
      });
      return;
    }

    const { data, error } = await supabase
      .from("sip_trunks")
      .insert({
        name: newTrunk.name,
        description: newTrunk.description || null,
        sip_server: newTrunk.sip_server || null,
        sip_username: newTrunk.sip_username || null,
        sip_password: newTrunk.sip_password || null,
      })
      .select()
      .single();

    if (error) {
      toast({
        title: "Error creating SIP trunk",
        description: error.message,
        variant: "destructive",
      });
    } else {
      setTrunks([data, ...trunks]);
      setNewTrunk({ name: "", description: "", sip_server: "", sip_username: "", sip_password: "" });
      setShowForm(false);
      toast({
        title: "SIP trunk created",
        description: "Your webhook URL is ready to use",
      });
    }
  };

  const toggleActive = async (id: string, currentState: boolean) => {
    const { error } = await supabase
      .from("sip_trunks")
      .update({ is_active: !currentState })
      .eq("id", id);

    if (error) {
      toast({
        title: "Error updating trunk",
        description: error.message,
        variant: "destructive",
      });
    } else {
      setTrunks(trunks.map(t => t.id === id ? { ...t, is_active: !currentState } : t));
    }
  };

  const deleteTrunk = async (id: string) => {
    const { error } = await supabase
      .from("sip_trunks")
      .delete()
      .eq("id", id);

    if (error) {
      toast({
        title: "Error deleting trunk",
        description: error.message,
        variant: "destructive",
      });
    } else {
      setTrunks(trunks.filter(t => t.id !== id));
      toast({ title: "SIP trunk deleted" });
    }
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
    toast({ title: "Copied to clipboard" });
  };

  const getWebhookUrl = (token: string) => {
    return `${supabaseUrl}/functions/v1/sip-incoming?token=${token}`;
  };

  return (
    <div className="min-h-screen bg-background p-6">
      <div className="max-w-4xl mx-auto space-y-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <Server className="h-8 w-8 text-primary" />
            <div>
              <h1 className="text-2xl font-bold">SIP Trunk Configuration</h1>
              <p className="text-muted-foreground">Connect your SIP provider to route calls to Ada</p>
            </div>
          </div>
          <Link to="/live">
            <Button variant="outline">
              <Phone className="h-4 w-4 mr-2" />
              Live Calls
            </Button>
          </Link>
          <Link to="/icabbi">
            <Button variant="outline">
              <Car className="h-4 w-4 mr-2" />
              iCabbi Settings
            </Button>
          </Link>
        </div>

        {/* How it works */}
        <Card>
          <CardHeader>
            <CardTitle className="text-lg">How it works</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm text-muted-foreground">
            <p>1. Create a SIP trunk configuration below</p>
            <p>2. Copy the webhook URL generated for your trunk</p>
            <p>3. Configure your SIP provider (Twilio, Telnyx, SIP Sourcery, etc.) to send calls to this webhook</p>
            <p>4. When a call comes in, Ada will handle it automatically</p>
          </CardContent>
        </Card>

        {/* iCabbi API Configuration */}
        <div>
          <div className="flex items-center gap-3 mb-4">
            <Car className="h-6 w-6 text-primary" />
            <h2 className="text-xl font-semibold">iCabbi API Configuration</h2>
          </div>

          {icabbiLoading ? (
            <Card><CardContent className="p-6 text-center text-muted-foreground">Loading...</CardContent></Card>
          ) : icabbiCompanies.length === 0 ? (
            <Card>
              <CardContent className="p-6 text-center text-muted-foreground">
                No companies configured. <Link to="/icabbi" className="text-primary underline">Add one in iCabbi Settings</Link>.
              </CardContent>
            </Card>
          ) : (
            <div className="space-y-3">
              {icabbiCompanies.map(company => (
                <Card key={company.id} className={!company.is_active ? "opacity-60" : ""}>
                  {/* Card Header — always visible */}
                  <CardHeader className="pb-3 cursor-pointer" onClick={() =>
                    setIcabbiExpanded(prev => ({ ...prev, [company.id]: !prev[company.id] }))
                  }>
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-3">
                        <Car className={`h-5 w-5 ${company.icabbi_enabled ? "text-primary" : "text-muted-foreground"}`} />
                        <div>
                          <div className="flex items-center gap-2">
                            <CardTitle className="text-base">{company.name}</CardTitle>
                            <Badge variant={company.icabbi_enabled ? "default" : "secondary"} className="text-xs">
                              {company.icabbi_enabled ? "iCabbi ON" : "iCabbi OFF"}
                            </Badge>
                          </div>
                          <CardDescription className="text-xs">
                            Site ID: {company.icabbi_site_id ?? "—"} · Company ID: {company.icabbi_company_id ?? "—"}
                          </CardDescription>
                        </div>
                      </div>
                      <div className="flex items-center gap-3">
                        <Switch
                          checked={company.icabbi_enabled}
                          onCheckedChange={v => {
                            updateIcabbiField(company.id, "icabbi_enabled", v);
                          }}
                          onClick={e => e.stopPropagation()}
                        />
                        {icabbiExpanded[company.id]
                          ? <ChevronUp className="h-4 w-4 text-muted-foreground" />
                          : <ChevronDown className="h-4 w-4 text-muted-foreground" />
                        }
                      </div>
                    </div>
                  </CardHeader>

                  {/* Expanded fields */}
                  {icabbiExpanded[company.id] && (
                    <CardContent className="space-y-4 pt-0">
                      <div className="grid grid-cols-2 gap-4">
                        <div className="space-y-2">
                          <Label className="text-sm">Site ID</Label>
                          <Input
                            type="number"
                            placeholder="71"
                            value={company.icabbi_site_id ?? ""}
                            onChange={e => updateIcabbiField(company.id, "icabbi_site_id", e.target.value ? parseInt(e.target.value) : null)}
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="text-sm">Company ID</Label>
                          <Input
                            placeholder="iCabbi company identifier"
                            value={company.icabbi_company_id ?? ""}
                            onChange={e => updateIcabbiField(company.id, "icabbi_company_id", e.target.value || null)}
                          />
                        </div>
                      </div>

                      <div className="space-y-2">
                        <Label className="text-sm">Tenant Base URL</Label>
                        <Input
                          placeholder="https://yourtenant.icabbi.net"
                          value={company.icabbi_tenant_base ?? ""}
                          onChange={e => updateIcabbiField(company.id, "icabbi_tenant_base", e.target.value)}
                        />
                      </div>

                      <div className="grid grid-cols-2 gap-4">
                        <div className="space-y-2">
                          <Label className="text-sm">App Key</Label>
                          <Input
                            placeholder="iCabbi App Key"
                            value={company.icabbi_app_key ?? ""}
                            onChange={e => updateIcabbiField(company.id, "icabbi_app_key", e.target.value || null)}
                          />
                        </div>
                        <div className="space-y-2">
                          <Label className="text-sm">Secret Key</Label>
                          <div className="relative">
                            <Input
                              type={icabbiSecrets[company.id] ? "text" : "password"}
                              placeholder="iCabbi Secret Key"
                              value={company.icabbi_secret_key ?? ""}
                              onChange={e => updateIcabbiField(company.id, "icabbi_secret_key", e.target.value || null)}
                              className="pr-10"
                            />
                            <Button
                              variant="ghost"
                              size="icon"
                              className="absolute right-1 top-1 h-7 w-7"
                              onClick={() => setIcabbiSecrets(prev => ({ ...prev, [company.id]: !prev[company.id] }))}
                              type="button"
                            >
                              {icabbiSecrets[company.id] ? <EyeOff className="h-3 w-3" /> : <Eye className="h-3 w-3" />}
                            </Button>
                          </div>
                        </div>
                      </div>

                      <Button
                        onClick={() => saveIcabbiCompany(company)}
                        disabled={icabbiSaving === company.id}
                        size="sm"
                        className="w-full"
                      >
                        <Save className="h-3 w-3 mr-2" />
                        {icabbiSaving === company.id ? "Saving..." : "Save iCabbi Settings"}
                      </Button>
                    </CardContent>
                  )}
                </Card>
              ))}
            </div>
          )}
        </div>

        {/* Add New Trunk */}
        {!showForm ? (
          <Button onClick={() => setShowForm(true)} className="w-full">
            <Plus className="h-4 w-4 mr-2" />
            Add SIP Trunk
          </Button>
        ) : (
          <Card>
            <CardHeader>
              <CardTitle>New SIP Trunk</CardTitle>
              <CardDescription>Configure your SIP connection details</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label htmlFor="name">Name *</Label>
                  <Input
                    id="name"
                    placeholder="My SIP Trunk"
                    value={newTrunk.name}
                    onChange={(e) => setNewTrunk({ ...newTrunk, name: e.target.value })}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="description">Description</Label>
                  <Input
                    id="description"
                    placeholder="Optional description"
                    value={newTrunk.description}
                    onChange={(e) => setNewTrunk({ ...newTrunk, description: e.target.value })}
                  />
                </div>
              </div>
              <div className="grid grid-cols-3 gap-4">
                <div className="space-y-2">
                  <Label htmlFor="server">SIP Server</Label>
                  <Input
                    id="server"
                    placeholder="sip.example.com"
                    value={newTrunk.sip_server}
                    onChange={(e) => setNewTrunk({ ...newTrunk, sip_server: e.target.value })}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="username">SIP Username</Label>
                  <Input
                    id="username"
                    placeholder="username"
                    value={newTrunk.sip_username}
                    onChange={(e) => setNewTrunk({ ...newTrunk, sip_username: e.target.value })}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="password">SIP Password</Label>
                  <Input
                    id="password"
                    type="password"
                    placeholder="password"
                    value={newTrunk.sip_password}
                    onChange={(e) => setNewTrunk({ ...newTrunk, sip_password: e.target.value })}
                  />
                </div>
              </div>
              <div className="flex gap-2">
                <Button onClick={createTrunk}>Create Trunk</Button>
                <Button variant="outline" onClick={() => setShowForm(false)}>Cancel</Button>
              </div>
            </CardContent>
          </Card>
        )}

        {/* Existing Trunks */}
        <div className="space-y-4">
          {loading ? (
            <Card>
              <CardContent className="p-8 text-center text-muted-foreground">
                Loading...
              </CardContent>
            </Card>
          ) : trunks.length === 0 ? (
            <Card>
              <CardContent className="p-8 text-center text-muted-foreground">
                No SIP trunks configured yet. Add one above to get started.
              </CardContent>
            </Card>
          ) : (
            trunks.map((trunk) => (
              <Card key={trunk.id} className={!trunk.is_active ? "opacity-60" : ""}>
                <CardHeader className="pb-3">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                      <Phone className={`h-5 w-5 ${trunk.is_active ? "text-green-500" : "text-muted-foreground"}`} />
                      <div>
                        <CardTitle className="text-lg">{trunk.name}</CardTitle>
                        {trunk.description && (
                          <CardDescription>{trunk.description}</CardDescription>
                        )}
                      </div>
                    </div>
                    <div className="flex items-center gap-4">
                      <div className="flex items-center gap-2">
                        <Label htmlFor={`active-${trunk.id}`} className="text-sm">Active</Label>
                        <Switch
                          id={`active-${trunk.id}`}
                          checked={trunk.is_active}
                          onCheckedChange={() => toggleActive(trunk.id, trunk.is_active)}
                        />
                      </div>
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => deleteTrunk(trunk.id)}
                        className="text-destructive hover:text-destructive"
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </div>
                </CardHeader>
                <CardContent className="space-y-1 bg-muted/30 rounded-lg p-4">
                  {/* Credential Rows */}
                  <CredentialRow 
                    label="Webhook URL" 
                    value={getWebhookUrl(trunk.webhook_token)} 
                    onCopy={() => copyToClipboard(getWebhookUrl(trunk.webhook_token))} 
                  />
                  {trunk.sip_server && (
                    <CredentialRow 
                      label="Registrar Hostname or IP" 
                      value={trunk.sip_server} 
                      onCopy={() => copyToClipboard(trunk.sip_server!)} 
                    />
                  )}
                  {trunk.sip_username && (
                    <CredentialRow 
                      label="Authentication ID" 
                      value={trunk.sip_username} 
                      onCopy={() => copyToClipboard(trunk.sip_username!)} 
                    />
                  )}
                  {trunk.sip_password && (
                    <CredentialRow 
                      label="Authentication Password" 
                      value={showPasswords[trunk.id] ? trunk.sip_password : "••••••••"} 
                      onCopy={() => copyToClipboard(trunk.sip_password!)}
                      showToggle
                      isVisible={showPasswords[trunk.id]}
                      onToggleVisibility={() => setShowPasswords({ ...showPasswords, [trunk.id]: !showPasswords[trunk.id] })}
                    />
                  )}
                </CardContent>
              </Card>
            ))
          )}
        </div>
      </div>
    </div>
  );
};

export default SipConfig;
