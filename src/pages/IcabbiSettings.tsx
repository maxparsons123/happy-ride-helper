import { useState, useEffect } from "react";
import { supabase } from "@/integrations/supabase/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Switch } from "@/components/ui/switch";
import { useToast } from "@/hooks/use-toast";
import { Building2, Plus, Save, Trash2, Eye, EyeOff, Car } from "lucide-react";
import { Link } from "react-router-dom";
import { Badge } from "@/components/ui/badge";

interface Company {
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
  contact_name: string | null;
  contact_phone: string | null;
  contact_email: string | null;
}

const emptyCompany: Omit<Company, "id" | "slug"> = {
  name: "",
  is_active: true,
  icabbi_enabled: false,
  icabbi_site_id: null,
  icabbi_company_id: null,
  icabbi_app_key: null,
  icabbi_secret_key: null,
  icabbi_tenant_base: "https://yourtenant.icabbi.net",
  contact_name: null,
  contact_phone: null,
  contact_email: null,
};

export default function IcabbiSettings() {
  const [companies, setCompanies] = useState<Company[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState<string | null>(null);
  const [showSecrets, setShowSecrets] = useState<Record<string, boolean>>({});
  const [showForm, setShowForm] = useState(false);
  const [newCompany, setNewCompany] = useState(emptyCompany);
  const { toast } = useToast();

  useEffect(() => {
    fetchCompanies();
  }, []);

  const fetchCompanies = async () => {
    const { data, error } = await supabase
      .from("companies")
      .select("id,name,slug,is_active,icabbi_enabled,icabbi_site_id,icabbi_company_id,icabbi_app_key,icabbi_secret_key,icabbi_tenant_base,contact_name,contact_phone,contact_email")
      .order("created_at", { ascending: false });

    if (error) {
      toast({ title: "Error loading companies", description: error.message, variant: "destructive" });
    } else {
      setCompanies((data as Company[]) || []);
    }
    setLoading(false);
  };

  const createCompany = async () => {
    if (!newCompany.name.trim()) {
      toast({ title: "Name required", variant: "destructive" });
      return;
    }
    const slug = newCompany.name.toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "");
    const { data, error } = await supabase
      .from("companies")
      .insert({ ...newCompany, slug })
      .select()
      .single();

    if (error) {
      toast({ title: "Error creating company", description: error.message, variant: "destructive" });
    } else {
      setCompanies([data as Company, ...companies]);
      setNewCompany(emptyCompany);
      setShowForm(false);
      toast({ title: "Company created" });
    }
  };

  const updateCompany = async (company: Company) => {
    setSaving(company.id);

    // Snapshot all editable fields at call time — avoids stale closure on setCompanies below
    const patch = {
      icabbi_enabled: company.icabbi_enabled,
      icabbi_site_id: company.icabbi_site_id ?? null,
      icabbi_company_id: company.icabbi_company_id || null,
      icabbi_app_key: company.icabbi_app_key || null,
      icabbi_secret_key: company.icabbi_secret_key || null,
      icabbi_tenant_base: company.icabbi_tenant_base || "https://yourtenant.icabbi.net",
      is_active: company.is_active,
      updated_at: new Date().toISOString(),
    };

    const { error } = await supabase
      .from("companies")
      .update(patch)
      .eq("id", company.id);

    if (error) {
      toast({ title: "Error saving", description: error.message, variant: "destructive" });
      // Re-fetch to restore server state so UI isn't out of sync
      await fetchCompanies();
    } else {
      // Use functional updater to avoid stale closure overwriting concurrent edits
      setCompanies(prev => prev.map(c => c.id === company.id ? { ...c, ...patch } : c));
      toast({ title: "Saved", description: `${company.name} iCabbi settings updated` });
    }
    setSaving(null);
  };

  const deleteCompany = async (id: string) => {
    const { error } = await supabase.from("companies").delete().eq("id", id);
    if (error) {
      toast({ title: "Error deleting", description: error.message, variant: "destructive" });
    } else {
      setCompanies(companies.filter(c => c.id !== id));
      toast({ title: "Company deleted" });
    }
  };

  const updateField = (id: string, field: keyof Company, value: unknown) => {
    setCompanies(companies.map(c => c.id === id ? { ...c, [field]: value } : c));
  };

  const toggleSecret = (id: string) => {
    setShowSecrets(prev => ({ ...prev, [id]: !prev[id] }));
  };

  return (
    <div className="min-h-screen bg-background p-6">
      <div className="max-w-4xl mx-auto space-y-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-3">
            <Car className="h-8 w-8 text-primary" />
            <div>
              <h1 className="text-2xl font-bold">iCabbi Dispatch Settings</h1>
              <p className="text-muted-foreground">Configure iCabbi API integration per company</p>
            </div>
          </div>
          <Link to="/sip-config">
            <Button variant="outline">SIP Config</Button>
          </Link>
        </div>

        {/* Add New Company */}
        {!showForm ? (
          <Button onClick={() => setShowForm(true)} className="w-full">
            <Plus className="h-4 w-4 mr-2" />
            Add Company
          </Button>
        ) : (
          <Card>
            <CardHeader>
              <CardTitle>New Company</CardTitle>
              <CardDescription>Add a company with iCabbi dispatch integration</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label>Company Name *</Label>
                  <Input
                    placeholder="e.g. Blackburn Radio Cars"
                    value={newCompany.name}
                    onChange={e => setNewCompany({ ...newCompany, name: e.target.value })}
                  />
                </div>
                <div className="space-y-2">
                  <Label>Tenant Base URL</Label>
                  <Input
                    placeholder="https://yourtenant.icabbi.net"
                    value={newCompany.icabbi_tenant_base ?? ""}
                    onChange={e => setNewCompany({ ...newCompany, icabbi_tenant_base: e.target.value })}
                  />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label>Site ID</Label>
                  <Input
                    type="number"
                    placeholder="71"
                    value={newCompany.icabbi_site_id ?? ""}
                    onChange={e => setNewCompany({ ...newCompany, icabbi_site_id: e.target.value ? parseInt(e.target.value) : null })}
                  />
                </div>
                <div className="space-y-2">
                  <Label>Company ID</Label>
                  <Input
                    placeholder="iCabbi company identifier"
                    value={newCompany.icabbi_company_id ?? ""}
                    onChange={e => setNewCompany({ ...newCompany, icabbi_company_id: e.target.value || null })}
                  />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-2">
                  <Label>App Key</Label>
                  <Input
                    placeholder="iCabbi App Key"
                    value={newCompany.icabbi_app_key ?? ""}
                    onChange={e => setNewCompany({ ...newCompany, icabbi_app_key: e.target.value || null })}
                  />
                </div>
                <div className="space-y-2">
                  <Label>Secret Key</Label>
                  <Input
                    type="password"
                    placeholder="iCabbi Secret Key"
                    value={newCompany.icabbi_secret_key ?? ""}
                    onChange={e => setNewCompany({ ...newCompany, icabbi_secret_key: e.target.value || null })}
                  />
                </div>
              </div>
              <div className="flex items-center gap-3">
                <Switch
                  checked={newCompany.icabbi_enabled}
                  onCheckedChange={v => setNewCompany({ ...newCompany, icabbi_enabled: v })}
                />
                <Label>Enable iCabbi integration</Label>
              </div>
              <div className="flex gap-2">
                <Button onClick={createCompany}>Create Company</Button>
                <Button variant="outline" onClick={() => setShowForm(false)}>Cancel</Button>
              </div>
            </CardContent>
          </Card>
        )}

        {/* Company Cards */}
        {loading ? (
          <Card><CardContent className="p-8 text-center text-muted-foreground">Loading...</CardContent></Card>
        ) : companies.length === 0 ? (
          <Card><CardContent className="p-8 text-center text-muted-foreground">No companies yet. Add one above.</CardContent></Card>
        ) : (
          <div className="space-y-4">
            {companies.map(company => (
              <Card key={company.id} className={!company.is_active ? "opacity-60" : ""}>
                <CardHeader className="pb-3">
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-3">
                      <Building2 className={`h-5 w-5 ${company.is_active ? "text-primary" : "text-muted-foreground"}`} />
                      <div>
                        <div className="flex items-center gap-2">
                          <CardTitle className="text-lg">{company.name}</CardTitle>
                          <Badge variant={company.icabbi_enabled ? "default" : "secondary"} className="text-xs">
                            {company.icabbi_enabled ? "iCabbi ON" : "iCabbi OFF"}
                          </Badge>
                          {!company.is_active && (
                            <Badge variant="outline" className="text-xs text-muted-foreground">Inactive</Badge>
                          )}
                        </div>
                        <CardDescription className="font-mono text-xs">{company.slug}</CardDescription>
                      </div>
                    </div>
                    <div className="flex items-center gap-2">
                      <Button
                        variant="ghost"
                        size="icon"
                        className="text-destructive hover:text-destructive"
                        onClick={() => deleteCompany(company.id)}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </div>
                </CardHeader>

                <CardContent className="space-y-4">
                  {/* Enable / Active toggles */}
                  <div className="flex gap-6">
                    <div className="flex items-center gap-2">
                      <Switch
                        checked={company.icabbi_enabled}
                        onCheckedChange={v => updateField(company.id, "icabbi_enabled", v)}
                      />
                      <Label className="text-sm">iCabbi Enabled</Label>
                    </div>
                    <div className="flex items-center gap-2">
                      <Switch
                        checked={company.is_active}
                        onCheckedChange={v => updateField(company.id, "is_active", v)}
                      />
                      <Label className="text-sm">Company Active</Label>
                    </div>
                  </div>

                  {/* iCabbi fields */}
                  <div className="grid grid-cols-2 gap-4">
                    <div className="space-y-2">
                      <Label className="text-sm">Site ID</Label>
                      <Input
                        type="number"
                        placeholder="71"
                        value={company.icabbi_site_id ?? ""}
                        onChange={e => updateField(company.id, "icabbi_site_id", e.target.value ? parseInt(e.target.value) : null)}
                      />
                    </div>
                    <div className="space-y-2">
                      <Label className="text-sm">Company ID</Label>
                      <Input
                        placeholder="iCabbi company identifier"
                        value={company.icabbi_company_id ?? ""}
                        onChange={e => updateField(company.id, "icabbi_company_id", e.target.value || null)}
                      />
                    </div>
                  </div>

                  <div className="space-y-2">
                    <Label className="text-sm">Tenant Base URL</Label>
                    <Input
                      placeholder="https://yourtenant.icabbi.net"
                      value={company.icabbi_tenant_base ?? ""}
                      onChange={e => updateField(company.id, "icabbi_tenant_base", e.target.value)}
                    />
                  </div>

                  <div className="grid grid-cols-2 gap-4">
                    <div className="space-y-2">
                      <Label className="text-sm">App Key</Label>
                      <Input
                        placeholder="iCabbi App Key"
                        value={company.icabbi_app_key ?? ""}
                        onChange={e => updateField(company.id, "icabbi_app_key", e.target.value || null)}
                      />
                    </div>
                    <div className="space-y-2">
                      <Label className="text-sm">Secret Key</Label>
                      <div className="relative">
                        <Input
                          type={showSecrets[company.id] ? "text" : "password"}
                          placeholder="iCabbi Secret Key"
                          value={company.icabbi_secret_key ?? ""}
                          onChange={e => updateField(company.id, "icabbi_secret_key", e.target.value || null)}
                          className="pr-10"
                        />
                        <Button
                          variant="ghost"
                          size="icon"
                          className="absolute right-1 top-1 h-7 w-7"
                          onClick={() => toggleSecret(company.id)}
                          type="button"
                        >
                          {showSecrets[company.id] ? <EyeOff className="h-3 w-3" /> : <Eye className="h-3 w-3" />}
                        </Button>
                      </div>
                    </div>
                  </div>

                  <Button
                    onClick={() => updateCompany(company)}
                    disabled={saving === company.id}
                    className="w-full"
                  >
                    <Save className="h-4 w-4 mr-2" />
                    {saving === company.id ? "Saving..." : "Save Changes"}
                  </Button>
                </CardContent>
              </Card>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
