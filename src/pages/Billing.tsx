import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { supabase } from "@/integrations/supabase/client";
import { Card } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { 
  Table, 
  TableBody, 
  TableCell, 
  TableHead, 
  TableHeader, 
  TableRow 
} from "@/components/ui/table";
import { 
  Select, 
  SelectContent, 
  SelectItem, 
  SelectTrigger, 
  SelectValue 
} from "@/components/ui/select";
import { 
  ArrowLeft, 
  Phone, 
  MapPin, 
  DollarSign, 
  Calendar, 
  Users, 
  Search,
  Download,
  RefreshCw,
  Building2,
  Plus
} from "lucide-react";
import { format, subDays, startOfDay, endOfDay } from "date-fns";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { toast } from "sonner";

interface Company {
  id: string;
  name: string;
  slug: string;
  webhook_url: string | null;
  is_active: boolean;
}

interface Booking {
  id: string;
  call_id: string;
  caller_phone: string;
  caller_name: string | null;
  pickup: string;
  pickup_name: string | null;
  destination: string;
  destination_name: string | null;
  passengers: number;
  fare: string | null;
  eta: string | null;
  status: string;
  booked_at: string;
  completed_at: string | null;
  cancelled_at: string | null;
  cancellation_reason: string | null;
  booking_details: Record<string, unknown> | null;
  company_id: string | null;
}

type DateFilter = "today" | "yesterday" | "7days" | "30days" | "all";

export default function Billing() {
  const [companies, setCompanies] = useState<Company[]>([]);
  const [selectedCompany, setSelectedCompany] = useState<string>("all");
  const [bookings, setBookings] = useState<Booking[]>([]);
  const [loading, setLoading] = useState(true);
  const [searchQuery, setSearchQuery] = useState("");
  const [dateFilter, setDateFilter] = useState<DateFilter>("today");
  const [statusFilter, setStatusFilter] = useState<string>("all");
  
  // New company dialog
  const [newCompanyOpen, setNewCompanyOpen] = useState(false);
  const [newCompanyName, setNewCompanyName] = useState("");
  const [newCompanyWebhook, setNewCompanyWebhook] = useState("");
  const [creatingCompany, setCreatingCompany] = useState(false);

  // Fetch companies
  useEffect(() => {
    const fetchCompanies = async () => {
      const { data, error } = await supabase
        .from("companies")
        .select("*")
        .eq("is_active", true)
        .order("name");
      
      if (!error && data) {
        setCompanies(data);
      }
    };
    fetchCompanies();
  }, []);

  const fetchBookings = async () => {
    setLoading(true);
    
    let query = supabase
      .from("bookings")
      .select("*")
      .order("booked_at", { ascending: false });

    // Apply company filter
    if (selectedCompany !== "all") {
      query = query.eq("company_id", selectedCompany);
    }

    // Apply date filter
    const now = new Date();
    if (dateFilter === "today") {
      query = query.gte("booked_at", startOfDay(now).toISOString());
    } else if (dateFilter === "yesterday") {
      const yesterday = subDays(now, 1);
      query = query
        .gte("booked_at", startOfDay(yesterday).toISOString())
        .lt("booked_at", startOfDay(now).toISOString());
    } else if (dateFilter === "7days") {
      query = query.gte("booked_at", subDays(now, 7).toISOString());
    } else if (dateFilter === "30days") {
      query = query.gte("booked_at", subDays(now, 30).toISOString());
    }

    // Apply status filter
    if (statusFilter !== "all") {
      query = query.eq("status", statusFilter);
    }

    const { data, error } = await query.limit(100);

    if (error) {
      console.error("Error fetching bookings:", error);
    } else {
      setBookings((data as Booking[]) || []);
    }
    setLoading(false);
  };

  useEffect(() => {
    fetchBookings();
  }, [dateFilter, statusFilter, selectedCompany]);

  // Filter by search query (client-side)
  const filteredBookings = bookings.filter((b) => {
    if (!searchQuery) return true;
    const q = searchQuery.toLowerCase();
    return (
      b.caller_phone?.toLowerCase().includes(q) ||
      b.caller_name?.toLowerCase().includes(q) ||
      b.pickup?.toLowerCase().includes(q) ||
      b.destination?.toLowerCase().includes(q) ||
      b.call_id?.toLowerCase().includes(q)
    );
  });

  // Calculate totals
  const totalBookings = filteredBookings.length;
  const completedBookings = filteredBookings.filter(b => b.status === "completed" || b.status === "active").length;
  const cancelledBookings = filteredBookings.filter(b => b.status === "cancelled").length;
  const totalFare = filteredBookings.reduce((sum, b) => {
    if (b.fare) {
      const fareNum = parseFloat(b.fare.replace(/[^0-9.]/g, ""));
      if (!isNaN(fareNum)) return sum + fareNum;
    }
    return sum;
  }, 0);

  const getStatusBadge = (status: string) => {
    switch (status) {
      case "completed":
        return <Badge className="bg-green-500/20 text-green-400 border-green-500/30">Completed</Badge>;
      case "active":
        return <Badge className="bg-blue-500/20 text-blue-400 border-blue-500/30">Active</Badge>;
      case "cancelled":
        return <Badge className="bg-red-500/20 text-red-400 border-red-500/30">Cancelled</Badge>;
      default:
        return <Badge variant="outline">{status}</Badge>;
    }
  };

  const exportToCsv = () => {
    const companyName = selectedCompany === "all" 
      ? "all-companies" 
      : companies.find(c => c.id === selectedCompany)?.slug || "unknown";
    
    const headers = ["Date", "Time", "Phone", "Name", "Pickup", "Destination", "Passengers", "Fare", "Status"];
    const rows = filteredBookings.map((b) => [
      format(new Date(b.booked_at), "yyyy-MM-dd"),
      format(new Date(b.booked_at), "HH:mm"),
      b.caller_phone,
      b.caller_name || "",
      b.pickup,
      b.destination,
      b.passengers,
      b.fare || "",
      b.status,
    ]);

    const csvContent = [headers.join(","), ...rows.map((r) => r.map((c) => `"${c}"`).join(","))].join("\n");
    const blob = new Blob([csvContent], { type: "text/csv" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `bookings-${companyName}-${format(new Date(), "yyyy-MM-dd")}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const createCompany = async () => {
    if (!newCompanyName.trim()) {
      toast.error("Company name is required");
      return;
    }

    setCreatingCompany(true);
    const slug = newCompanyName.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "");
    
    const { data, error } = await supabase
      .from("companies")
      .insert({
        name: newCompanyName.trim(),
        slug,
        webhook_url: newCompanyWebhook.trim() || null,
      })
      .select()
      .single();

    if (error) {
      console.error("Error creating company:", error);
      toast.error("Failed to create company");
    } else if (data) {
      setCompanies(prev => [...prev, data].sort((a, b) => a.name.localeCompare(b.name)));
      setSelectedCompany(data.id);
      setNewCompanyOpen(false);
      setNewCompanyName("");
      setNewCompanyWebhook("");
      toast.success(`Company "${data.name}" created`);
    }
    setCreatingCompany(false);
  };

  const selectedCompanyData = companies.find(c => c.id === selectedCompany);

  return (
    <div className="min-h-screen bg-background text-foreground p-4 md:p-6">
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-4">
          <Link to="/live">
            <Button variant="ghost" size="icon">
              <ArrowLeft className="h-5 w-5" />
            </Button>
          </Link>
          <div>
            <h1 className="text-2xl font-bold">Billing & History</h1>
            <p className="text-muted-foreground text-sm">
              {selectedCompanyData ? selectedCompanyData.name : "All Companies"}
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={fetchBookings} disabled={loading}>
            <RefreshCw className={`h-4 w-4 mr-2 ${loading ? "animate-spin" : ""}`} />
            Refresh
          </Button>
          <Button variant="outline" size="sm" onClick={exportToCsv} disabled={filteredBookings.length === 0}>
            <Download className="h-4 w-4 mr-2" />
            Export CSV
          </Button>
        </div>
      </div>

      {/* Company Selector */}
      <Card className="p-4 mb-6 bg-card border-border">
        <div className="flex items-center gap-4">
          <div className="flex items-center gap-2">
            <Building2 className="h-5 w-5 text-muted-foreground" />
            <span className="text-sm font-medium">Company:</span>
          </div>
          <Select value={selectedCompany} onValueChange={setSelectedCompany}>
            <SelectTrigger className="w-[200px]">
              <SelectValue placeholder="Select company" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All Companies</SelectItem>
              {companies.map((company) => (
                <SelectItem key={company.id} value={company.id}>
                  {company.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          
          <Dialog open={newCompanyOpen} onOpenChange={setNewCompanyOpen}>
            <DialogTrigger asChild>
              <Button variant="outline" size="sm">
                <Plus className="h-4 w-4 mr-2" />
                Add Company
              </Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Add New Company</DialogTitle>
                <DialogDescription>
                  Create a new company with its own WebSocket stream for real-time calls.
                </DialogDescription>
              </DialogHeader>
              <div className="grid gap-4 py-4">
                <div className="grid gap-2">
                  <Label htmlFor="company-name">Company Name</Label>
                  <Input
                    id="company-name"
                    placeholder="e.g., ABC Taxis"
                    value={newCompanyName}
                    onChange={(e) => setNewCompanyName(e.target.value)}
                  />
                </div>
                <div className="grid gap-2">
                  <Label htmlFor="webhook-url">Webhook URL (optional)</Label>
                  <Input
                    id="webhook-url"
                    placeholder="https://dispatch.example.com/webhook"
                    value={newCompanyWebhook}
                    onChange={(e) => setNewCompanyWebhook(e.target.value)}
                  />
                  <p className="text-xs text-muted-foreground">
                    Booking data will be POSTed to this URL in real-time.
                  </p>
                </div>
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setNewCompanyOpen(false)}>
                  Cancel
                </Button>
                <Button onClick={createCompany} disabled={creatingCompany}>
                  {creatingCompany ? "Creating..." : "Create Company"}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          {selectedCompanyData?.webhook_url && (
            <Badge variant="outline" className="text-green-400 border-green-400/30">
              <span className="w-2 h-2 bg-green-400 rounded-full mr-2 animate-pulse" />
              WebSocket Active
            </Badge>
          )}
        </div>
      </Card>

      {/* Summary Cards */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-6">
        <Card className="p-4 bg-card border-border">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-primary/10">
              <Phone className="h-5 w-5 text-primary" />
            </div>
            <div>
              <p className="text-2xl font-bold">{totalBookings}</p>
              <p className="text-xs text-muted-foreground">Total Bookings</p>
            </div>
          </div>
        </Card>
        <Card className="p-4 bg-card border-border">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-green-500/10">
              <MapPin className="h-5 w-5 text-green-500" />
            </div>
            <div>
              <p className="text-2xl font-bold">{completedBookings}</p>
              <p className="text-xs text-muted-foreground">Completed</p>
            </div>
          </div>
        </Card>
        <Card className="p-4 bg-card border-border">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-red-500/10">
              <Phone className="h-5 w-5 text-red-500" />
            </div>
            <div>
              <p className="text-2xl font-bold">{cancelledBookings}</p>
              <p className="text-xs text-muted-foreground">Cancelled</p>
            </div>
          </div>
        </Card>
        <Card className="p-4 bg-card border-border">
          <div className="flex items-center gap-3">
            <div className="p-2 rounded-lg bg-amber-500/10">
              <DollarSign className="h-5 w-5 text-amber-500" />
            </div>
            <div>
              <p className="text-2xl font-bold">£{totalFare.toFixed(2)}</p>
              <p className="text-xs text-muted-foreground">Total Revenue</p>
            </div>
          </div>
        </Card>
      </div>

      {/* Filters */}
      <Card className="p-4 mb-6 bg-card border-border">
        <div className="flex flex-col md:flex-row gap-4">
          <div className="flex-1 relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <Input
              placeholder="Search by phone, name, address..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="pl-9"
            />
          </div>
          <Select value={dateFilter} onValueChange={(v) => setDateFilter(v as DateFilter)}>
            <SelectTrigger className="w-[160px]">
              <Calendar className="h-4 w-4 mr-2" />
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="today">Today</SelectItem>
              <SelectItem value="yesterday">Yesterday</SelectItem>
              <SelectItem value="7days">Last 7 Days</SelectItem>
              <SelectItem value="30days">Last 30 Days</SelectItem>
              <SelectItem value="all">All Time</SelectItem>
            </SelectContent>
          </Select>
          <Select value={statusFilter} onValueChange={setStatusFilter}>
            <SelectTrigger className="w-[140px]">
              <SelectValue placeholder="Status" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All Status</SelectItem>
              <SelectItem value="completed">Completed</SelectItem>
              <SelectItem value="active">Active</SelectItem>
              <SelectItem value="cancelled">Cancelled</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </Card>

      {/* Table */}
      <Card className="bg-card border-border overflow-hidden">
        <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow className="border-border hover:bg-muted/50">
                <TableHead className="text-muted-foreground">Date/Time</TableHead>
                <TableHead className="text-muted-foreground">Caller</TableHead>
                <TableHead className="text-muted-foreground">Pickup</TableHead>
                <TableHead className="text-muted-foreground">Destination</TableHead>
                <TableHead className="text-muted-foreground text-center">
                  <Users className="h-4 w-4 inline" />
                </TableHead>
                <TableHead className="text-muted-foreground">Fare</TableHead>
                <TableHead className="text-muted-foreground">Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading ? (
                <TableRow>
                  <TableCell colSpan={7} className="text-center py-8 text-muted-foreground">
                    Loading...
                  </TableCell>
                </TableRow>
              ) : filteredBookings.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={7} className="text-center py-8 text-muted-foreground">
                    No bookings found
                  </TableCell>
                </TableRow>
              ) : (
                filteredBookings.map((booking) => (
                  <TableRow key={booking.id} className="border-border hover:bg-muted/30">
                    <TableCell className="whitespace-nowrap">
                      <div className="text-sm">{format(new Date(booking.booked_at), "MMM d, yyyy")}</div>
                      <div className="text-xs text-muted-foreground">
                        {format(new Date(booking.booked_at), "HH:mm")}
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="text-sm font-medium">{booking.caller_name || "Unknown"}</div>
                      <div className="text-xs text-muted-foreground">{booking.caller_phone}</div>
                    </TableCell>
                    <TableCell className="max-w-[200px]">
                      <div className="text-sm truncate" title={booking.pickup}>
                        {booking.pickup_name || booking.pickup}
                      </div>
                    </TableCell>
                    <TableCell className="max-w-[200px]">
                      <div className="text-sm truncate" title={booking.destination}>
                        {booking.destination_name || booking.destination}
                      </div>
                    </TableCell>
                    <TableCell className="text-center">
                      <span className="text-sm">{booking.passengers}</span>
                    </TableCell>
                    <TableCell>
                      <span className="text-sm font-medium">
                        {booking.fare ? `£${booking.fare}` : "—"}
                      </span>
                    </TableCell>
                    <TableCell>{getStatusBadge(booking.status)}</TableCell>
                  </TableRow>
                ))
              )}
            </TableBody>
          </Table>
        </div>
      </Card>
    </div>
  );
}
