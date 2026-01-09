import { useState, useEffect } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Switch } from "@/components/ui/switch";
import { useToast } from "@/hooks/use-toast";
import { Phone, Plus, Trash2, Copy, Server, Eye, EyeOff } from "lucide-react";
import { Link } from "react-router-dom";

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
  const { toast } = useToast();

  const supabaseUrl = import.meta.env.VITE_SUPABASE_URL;

  useEffect(() => {
    fetchTrunks();
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
