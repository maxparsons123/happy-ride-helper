import { useState } from 'react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Save, Loader2 } from 'lucide-react';
import { supabase } from '@/integrations/supabase/client';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';

interface CompanyFormData {
  name: string;
  slug: string;
  address: string;
  contact_name: string;
  contact_phone: string;
  contact_email: string;
  api_key: string;
  api_endpoint: string;
  webhook_url: string;
  is_active: boolean;
  opening_hours: {
    mon?: string;
    tue?: string;
    wed?: string;
    thu?: string;
    fri?: string;
    sat?: string;
    sun?: string;
  };
}

const DAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'] as const;
const DAY_LABELS: Record<string, string> = {
  mon: 'Monday', tue: 'Tuesday', wed: 'Wednesday', thu: 'Thursday',
  fri: 'Friday', sat: 'Saturday', sun: 'Sunday',
};

const defaultForm: CompanyFormData = {
  name: '', slug: '', address: '', contact_name: '', contact_phone: '',
  contact_email: '', api_key: '', api_endpoint: '', webhook_url: '',
  is_active: true,
  opening_hours: {
    mon: '06:00-23:00', tue: '06:00-23:00', wed: '06:00-23:00',
    thu: '06:00-23:00', fri: '06:00-00:00', sat: '06:00-00:00', sun: '07:00-23:00',
  },
};

interface CompanyModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CompanyModal({ open, onOpenChange }: CompanyModalProps) {
  const [form, setForm] = useState<CompanyFormData>({ ...defaultForm });
  const [saving, setSaving] = useState(false);
  const qc = useQueryClient();

  const update = (field: keyof CompanyFormData, value: any) =>
    setForm(prev => ({ ...prev, [field]: value }));

  const updateHours = (day: string, value: string) =>
    setForm(prev => ({ ...prev, opening_hours: { ...prev.opening_hours, [day]: value } }));

  const handleNameChange = (name: string) => {
    const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
    setForm(prev => ({ ...prev, name, slug }));
  };

  const handleSave = async () => {
    if (!form.name.trim()) { toast.error('Company name is required'); return; }
    if (!form.slug.trim()) { toast.error('Slug is required'); return; }

    setSaving(true);
    const { error } = await supabase.from('companies').insert({
      name: form.name.trim(),
      slug: form.slug.trim(),
      address: form.address || null,
      contact_name: form.contact_name || null,
      contact_phone: form.contact_phone || null,
      contact_email: form.contact_email || null,
      api_key: form.api_key || null,
      api_endpoint: form.api_endpoint || null,
      webhook_url: form.webhook_url || null,
      is_active: form.is_active,
      opening_hours: form.opening_hours,
    } as any);
    setSaving(false);

    if (error) {
      toast.error('Failed to create company: ' + error.message);
    } else {
      toast.success('Company created');
      qc.invalidateQueries({ queryKey: ['companies'] });
      setForm({ ...defaultForm });
      onOpenChange(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Add Company</DialogTitle>
        </DialogHeader>

        <div className="space-y-4 pt-2">
          {/* Basic Info */}
          <div className="space-y-3">
            <h4 className="text-xs font-semibold uppercase text-muted-foreground tracking-wider">Company Details</h4>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <Label className="text-xs">Company Name *</Label>
                <Input value={form.name} onChange={e => handleNameChange(e.target.value)} placeholder="e.g. ABC Taxis" className="h-8 text-sm" />
              </div>
              <div>
                <Label className="text-xs">Slug</Label>
                <Input value={form.slug} onChange={e => update('slug', e.target.value)} placeholder="abc-taxis" className="h-8 text-sm" />
              </div>
            </div>
            <div>
              <Label className="text-xs">Address</Label>
              <Input value={form.address} onChange={e => update('address', e.target.value)} placeholder="123 High Street, Coventry" className="h-8 text-sm" />
            </div>
          </div>

          {/* Contact */}
          <div className="space-y-3">
            <h4 className="text-xs font-semibold uppercase text-muted-foreground tracking-wider">Contact Details</h4>
            <div>
              <Label className="text-xs">Contact Name</Label>
              <Input value={form.contact_name} onChange={e => update('contact_name', e.target.value)} placeholder="John Smith" className="h-8 text-sm" />
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <Label className="text-xs">Phone</Label>
                <Input value={form.contact_phone} onChange={e => update('contact_phone', e.target.value)} placeholder="07700 900000" className="h-8 text-sm" />
              </div>
              <div>
                <Label className="text-xs">Email</Label>
                <Input value={form.contact_email} onChange={e => update('contact_email', e.target.value)} placeholder="info@abctaxis.com" className="h-8 text-sm" />
              </div>
            </div>
          </div>

          {/* API Integration */}
          <div className="space-y-3">
            <h4 className="text-xs font-semibold uppercase text-muted-foreground tracking-wider">API Integration</h4>
            <div>
              <Label className="text-xs">API Key</Label>
              <Input value={form.api_key} onChange={e => update('api_key', e.target.value)} placeholder="sk-..." className="h-8 text-sm font-mono" type="password" />
            </div>
            <div>
              <Label className="text-xs">API Endpoint</Label>
              <Input value={form.api_endpoint} onChange={e => update('api_endpoint', e.target.value)} placeholder="https://api.abctaxis.com/bookings" className="h-8 text-sm" />
            </div>
            <div>
              <Label className="text-xs">Webhook URL</Label>
              <Input value={form.webhook_url} onChange={e => update('webhook_url', e.target.value)} placeholder="https://api.abctaxis.com/webhook" className="h-8 text-sm" />
            </div>
          </div>

          {/* Opening Hours */}
          <div className="space-y-3">
            <h4 className="text-xs font-semibold uppercase text-muted-foreground tracking-wider">Opening Hours</h4>
            <div className="space-y-1.5">
              {DAYS.map(day => (
                <div key={day} className="flex items-center gap-2">
                  <span className="text-xs w-20 text-muted-foreground">{DAY_LABELS[day]}</span>
                  <Input
                    value={form.opening_hours[day] || ''}
                    onChange={e => updateHours(day, e.target.value)}
                    placeholder="06:00-23:00"
                    className="h-7 text-xs flex-1"
                  />
                </div>
              ))}
            </div>
          </div>

          {/* Active */}
          <div className="flex items-center gap-2">
            <Switch checked={form.is_active} onCheckedChange={v => update('is_active', v)} />
            <Label className="text-xs">Active</Label>
          </div>

          <Button className="w-full" onClick={handleSave} disabled={saving}>
            {saving ? <Loader2 className="w-4 h-4 mr-1 animate-spin" /> : <Save className="w-4 h-4 mr-1" />}
            Create Company
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
