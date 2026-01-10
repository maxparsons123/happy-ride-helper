import { useState, useEffect } from "react";
import { Link } from "react-router-dom";
import { supabase } from "@/integrations/supabase/client";
import { useToast } from "@/hooks/use-toast";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Car, Plus, Save, Trash2, Radio, Server, Mic, Users, Bot, Sparkles, ArrowLeft } from "lucide-react";

interface Agent {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  system_prompt: string;
  voice: string;
  company_name: string;
  personality_traits: string[];
  greeting_style: string | null;
  language: string;
  is_active: boolean;
  created_at: string;
  updated_at: string;
}

const VOICE_OPTIONS = [
  { value: "shimmer", label: "Shimmer", description: "Warm, British female" },
  { value: "alloy", label: "Alloy", description: "Neutral, versatile" },
  { value: "echo", label: "Echo", description: "Male, clear" },
  { value: "fable", label: "Fable", description: "British, expressive" },
  { value: "onyx", label: "Onyx", description: "Deep male voice" },
  { value: "nova", label: "Nova", description: "Friendly, upbeat female" },
];

const DEFAULT_PROMPT = `You are {{agent_name}}, a friendly and professional Taxi Dispatcher for "{{company_name}}" taking phone calls.

PERSONALITY:
- {{personality_description}}
- Keep responses SHORT (1-2 sentences max) - this is a phone call
- Be efficient but personable

BOOKING FLOW - FOLLOW THIS EXACTLY:
1. Greet the customer (get their name if new)
2. Ask: "When do you need the taxi? Is it for now or a later time?"
3. Ask: "Where would you like to be picked up from?"
4. Ask: "And where are you heading to?"
5. Ask: "How many passengers?"
6. Once you have ALL 4 details (time, pickup, destination, passengers), do ONE confirmation
7. After confirmation, book the taxi

CRITICAL: Respond with ONLY valid JSON:
{"response":"your short message","pickup":"value or null","destination":"value or null","passengers":"number or null","status":"collecting or confirmed"}`;

export default function Agents() {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<Agent | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [newAgentName, setNewAgentName] = useState("");
  const [showCreateDialog, setShowCreateDialog] = useState(false);
  const { toast } = useToast();

  useEffect(() => {
    fetchAgents();
  }, []);

  const fetchAgents = async () => {
    try {
      const { data, error } = await supabase
        .from("agents")
        .select("*")
        .order("created_at", { ascending: true });

      if (error) throw error;

      // Parse personality_traits from JSON if needed
      const parsedAgents = (data || []).map(agent => ({
        ...agent,
        personality_traits: Array.isArray(agent.personality_traits) 
          ? agent.personality_traits 
          : JSON.parse(agent.personality_traits as string || "[]")
      }));

      setAgents(parsedAgents);
      if (parsedAgents.length > 0 && !selectedAgent) {
        setSelectedAgent(parsedAgents[0]);
      }
    } catch (error) {
      console.error("Error fetching agents:", error);
      toast({
        title: "Error",
        description: "Failed to load agents",
        variant: "destructive",
      });
    } finally {
      setIsLoading(false);
    }
  };

  const createAgent = async () => {
    if (!newAgentName.trim()) return;

    setIsCreating(true);
    try {
      const slug = newAgentName.toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "");
      
      const { data, error } = await supabase
        .from("agents")
        .insert({
          name: newAgentName,
          slug,
          description: `New agent - ${newAgentName}`,
          system_prompt: DEFAULT_PROMPT.replace(/\{\{agent_name\}\}/g, newAgentName),
          voice: "shimmer",
          company_name: "Imtech Taxi",
          personality_traits: ["friendly", "professional"],
          greeting_style: "Warm greeting",
          language: "en-GB",
          is_active: true,
        })
        .select()
        .single();

      if (error) throw error;

      const newAgent = {
        ...data,
        personality_traits: data.personality_traits as string[]
      };

      setAgents([...agents, newAgent]);
      setSelectedAgent(newAgent);
      setShowCreateDialog(false);
      setNewAgentName("");

      toast({
        title: "Agent Created",
        description: `${newAgentName} is ready to configure`,
      });
    } catch (error) {
      console.error("Error creating agent:", error);
      toast({
        title: "Error",
        description: "Failed to create agent",
        variant: "destructive",
      });
    } finally {
      setIsCreating(false);
    }
  };

  const saveAgent = async () => {
    if (!selectedAgent) return;

    setIsSaving(true);
    try {
      const { error } = await supabase
        .from("agents")
        .update({
          name: selectedAgent.name,
          description: selectedAgent.description,
          system_prompt: selectedAgent.system_prompt,
          voice: selectedAgent.voice,
          company_name: selectedAgent.company_name,
          personality_traits: selectedAgent.personality_traits,
          greeting_style: selectedAgent.greeting_style,
          language: selectedAgent.language,
          is_active: selectedAgent.is_active,
          updated_at: new Date().toISOString(),
        })
        .eq("id", selectedAgent.id);

      if (error) throw error;

      setAgents(agents.map(a => a.id === selectedAgent.id ? selectedAgent : a));

      toast({
        title: "Saved",
        description: `${selectedAgent.name}'s configuration updated`,
      });
    } catch (error) {
      console.error("Error saving agent:", error);
      toast({
        title: "Error",
        description: "Failed to save agent",
        variant: "destructive",
      });
    } finally {
      setIsSaving(false);
    }
  };

  const deleteAgent = async (agent: Agent) => {
    if (agents.length <= 1) {
      toast({
        title: "Cannot Delete",
        description: "You must have at least one agent",
        variant: "destructive",
      });
      return;
    }

    try {
      const { error } = await supabase
        .from("agents")
        .delete()
        .eq("id", agent.id);

      if (error) throw error;

      const updatedAgents = agents.filter(a => a.id !== agent.id);
      setAgents(updatedAgents);
      if (selectedAgent?.id === agent.id) {
        setSelectedAgent(updatedAgents[0] || null);
      }

      toast({
        title: "Deleted",
        description: `${agent.name} has been removed`,
      });
    } catch (error) {
      console.error("Error deleting agent:", error);
      toast({
        title: "Error",
        description: "Failed to delete agent",
        variant: "destructive",
      });
    }
  };

  const updateSelectedAgent = (updates: Partial<Agent>) => {
    if (!selectedAgent) return;
    setSelectedAgent({ ...selectedAgent, ...updates });
  };

  if (isLoading) {
    return (
      <div className="flex h-screen items-center justify-center bg-gradient-dark">
        <div className="animate-pulse text-muted-foreground">Loading agents...</div>
      </div>
    );
  }

  return (
    <div className="flex h-screen w-full bg-gradient-dark">
      {/* Agent Sidebar */}
      <aside className="w-72 border-r border-chat-border bg-card/50 backdrop-blur-sm flex flex-col">
        <div className="p-4 border-b border-chat-border">
          <div className="flex items-center justify-between mb-4">
            <div className="flex items-center gap-2">
              <Users className="h-5 w-5 text-primary" />
              <h2 className="font-display font-semibold text-foreground">Agents</h2>
            </div>
            <Dialog open={showCreateDialog} onOpenChange={setShowCreateDialog}>
              <DialogTrigger asChild>
                <Button size="sm" className="bg-gradient-gold hover:opacity-90">
                  <Plus className="h-4 w-4" />
                </Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Create New Agent</DialogTitle>
                  <DialogDescription>
                    Give your new agent a name. You can customize their personality after.
                  </DialogDescription>
                </DialogHeader>
                <div className="py-4">
                  <Label htmlFor="agent-name">Agent Name</Label>
                  <Input
                    id="agent-name"
                    placeholder="e.g., Sophie, Max, Charlie..."
                    value={newAgentName}
                    onChange={(e) => setNewAgentName(e.target.value)}
                    onKeyDown={(e) => e.key === "Enter" && createAgent()}
                  />
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setShowCreateDialog(false)}>
                    Cancel
                  </Button>
                  <Button onClick={createAgent} disabled={isCreating || !newAgentName.trim()}>
                    {isCreating ? "Creating..." : "Create Agent"}
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>
          <p className="text-xs text-muted-foreground">
            Configure AI personalities for your taxi dispatch
          </p>
        </div>

        <ScrollArea className="flex-1">
          <div className="p-2 space-y-1">
            {agents.map((agent) => (
              <button
                key={agent.id}
                onClick={() => setSelectedAgent(agent)}
                className={`w-full text-left p-3 rounded-lg transition-all ${
                  selectedAgent?.id === agent.id
                    ? "bg-primary/10 border border-primary/30"
                    : "hover:bg-accent/50 border border-transparent"
                }`}
              >
                <div className="flex items-center gap-3">
                  <div className={`h-10 w-10 rounded-full flex items-center justify-center ${
                    selectedAgent?.id === agent.id ? "bg-gradient-gold" : "bg-muted"
                  }`}>
                    <Bot className={`h-5 w-5 ${selectedAgent?.id === agent.id ? "text-primary-foreground" : "text-muted-foreground"}`} />
                  </div>
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-foreground truncate">{agent.name}</span>
                      {agent.is_active && (
                        <Badge variant="outline" className="text-xs text-green-400 border-green-400/30">
                          Active
                        </Badge>
                      )}
                    </div>
                    <p className="text-xs text-muted-foreground truncate">{agent.description}</p>
                  </div>
                </div>
              </button>
            ))}
          </div>
        </ScrollArea>

        <div className="p-4 border-t border-chat-border space-y-2">
          <Button variant="ghost" size="sm" asChild className="w-full justify-start text-muted-foreground hover:text-foreground">
            <Link to="/">
              <ArrowLeft className="mr-2 h-4 w-4" />
              Back to Chat
            </Link>
          </Button>
          <Button variant="ghost" size="sm" asChild className="w-full justify-start text-muted-foreground hover:text-foreground">
            <Link to="/live">
              <Radio className="mr-2 h-4 w-4" />
              Live Calls
            </Link>
          </Button>
          <Button variant="ghost" size="sm" asChild className="w-full justify-start text-muted-foreground hover:text-foreground">
            <Link to="/voice-test">
              <Mic className="mr-2 h-4 w-4" />
              Voice Test
            </Link>
          </Button>
        </div>
      </aside>

      {/* Main Content */}
      <main className="flex-1 flex flex-col overflow-hidden">
        {/* Header */}
        <header className="flex items-center justify-between border-b border-chat-border bg-card/80 backdrop-blur-sm px-6 py-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-full bg-gradient-gold shadow-glow">
              <Sparkles className="h-5 w-5 text-primary-foreground" />
            </div>
            <div>
              <h1 className="font-display text-lg font-semibold text-foreground">
                {selectedAgent?.name || "Agent"} Configuration
              </h1>
              <p className="text-xs text-muted-foreground">
                Customize personality, voice, and behavior
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            {selectedAgent && (
              <>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => deleteAgent(selectedAgent)}
                  className="text-destructive hover:text-destructive hover:bg-destructive/10"
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
                <Button onClick={saveAgent} disabled={isSaving} className="bg-gradient-gold hover:opacity-90">
                  <Save className="mr-2 h-4 w-4" />
                  {isSaving ? "Saving..." : "Save Changes"}
                </Button>
              </>
            )}
          </div>
        </header>

        {/* Editor */}
        {selectedAgent ? (
          <div className="flex-1 overflow-y-auto p-6">
            <Tabs defaultValue="personality" className="h-full">
              <TabsList className="mb-6">
                <TabsTrigger value="personality">Personality</TabsTrigger>
                <TabsTrigger value="prompt">System Prompt</TabsTrigger>
                <TabsTrigger value="settings">Settings</TabsTrigger>
              </TabsList>

              <TabsContent value="personality" className="space-y-6">
                <div className="grid gap-6 md:grid-cols-2">
                  <Card className="bg-card/50 border-chat-border">
                    <CardHeader>
                      <CardTitle className="text-base">Basic Info</CardTitle>
                      <CardDescription>Agent identity and branding</CardDescription>
                    </CardHeader>
                    <CardContent className="space-y-4">
                      <div className="space-y-2">
                        <Label htmlFor="name">Name</Label>
                        <Input
                          id="name"
                          value={selectedAgent.name}
                          onChange={(e) => updateSelectedAgent({ name: e.target.value })}
                          placeholder="Agent name"
                        />
                      </div>
                      <div className="space-y-2">
                        <Label htmlFor="description">Description</Label>
                        <Input
                          id="description"
                          value={selectedAgent.description || ""}
                          onChange={(e) => updateSelectedAgent({ description: e.target.value })}
                          placeholder="Brief description of this agent"
                        />
                      </div>
                      <div className="space-y-2">
                        <Label htmlFor="company">Company Name</Label>
                        <Input
                          id="company"
                          value={selectedAgent.company_name}
                          onChange={(e) => updateSelectedAgent({ company_name: e.target.value })}
                          placeholder="Taxi company name"
                        />
                      </div>
                    </CardContent>
                  </Card>

                  <Card className="bg-card/50 border-chat-border">
                    <CardHeader>
                      <CardTitle className="text-base">Voice & Style</CardTitle>
                      <CardDescription>How the agent sounds and behaves</CardDescription>
                    </CardHeader>
                    <CardContent className="space-y-4">
                      <div className="space-y-2">
                        <Label htmlFor="voice">Voice</Label>
                        <Select
                          value={selectedAgent.voice}
                          onValueChange={(value) => updateSelectedAgent({ voice: value })}
                        >
                          <SelectTrigger>
                            <SelectValue placeholder="Select voice" />
                          </SelectTrigger>
                          <SelectContent>
                            {VOICE_OPTIONS.map((voice) => (
                              <SelectItem key={voice.value} value={voice.value}>
                                <div className="flex flex-col">
                                  <span>{voice.label}</span>
                                  <span className="text-xs text-muted-foreground">{voice.description}</span>
                                </div>
                              </SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                      </div>
                      <div className="space-y-2">
                        <Label htmlFor="greeting">Greeting Style</Label>
                        <Textarea
                          id="greeting"
                          value={selectedAgent.greeting_style || ""}
                          onChange={(e) => updateSelectedAgent({ greeting_style: e.target.value })}
                          placeholder="How should this agent greet callers?"
                          rows={3}
                        />
                      </div>
                      <div className="space-y-2">
                        <Label>Personality Traits</Label>
                        <Input
                          value={selectedAgent.personality_traits.join(", ")}
                          onChange={(e) => updateSelectedAgent({ 
                            personality_traits: e.target.value.split(",").map(t => t.trim()).filter(Boolean) 
                          })}
                          placeholder="friendly, professional, warm..."
                        />
                        <p className="text-xs text-muted-foreground">Comma-separated traits</p>
                      </div>
                    </CardContent>
                  </Card>
                </div>
              </TabsContent>

              <TabsContent value="prompt" className="space-y-4">
                <Card className="bg-card/50 border-chat-border h-[calc(100vh-300px)]">
                  <CardHeader>
                    <CardTitle className="text-base">System Prompt</CardTitle>
                    <CardDescription>
                      The full instructions given to the AI. Use {"{{agent_name}}"}, {"{{company_name}}"}, and {"{{personality_description}}"} as placeholders.
                    </CardDescription>
                  </CardHeader>
                  <CardContent>
                    <Textarea
                      value={selectedAgent.system_prompt}
                      onChange={(e) => updateSelectedAgent({ system_prompt: e.target.value })}
                      className="min-h-[400px] font-mono text-sm"
                      placeholder="Enter the system prompt..."
                    />
                  </CardContent>
                </Card>
              </TabsContent>

              <TabsContent value="settings" className="space-y-6">
                <Card className="bg-card/50 border-chat-border">
                  <CardHeader>
                    <CardTitle className="text-base">Agent Settings</CardTitle>
                    <CardDescription>Control agent behavior and availability</CardDescription>
                  </CardHeader>
                  <CardContent className="space-y-4">
                    <div className="flex items-center justify-between">
                      <div>
                        <Label>Active</Label>
                        <p className="text-xs text-muted-foreground">Enable this agent for incoming calls</p>
                      </div>
                      <Switch
                        checked={selectedAgent.is_active}
                        onCheckedChange={(checked) => updateSelectedAgent({ is_active: checked })}
                      />
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="language">Language</Label>
                      <Select
                        value={selectedAgent.language}
                        onValueChange={(value) => updateSelectedAgent({ language: value })}
                      >
                        <SelectTrigger>
                          <SelectValue placeholder="Select language" />
                        </SelectTrigger>
                        <SelectContent>
                          <SelectItem value="en-GB">English (British)</SelectItem>
                          <SelectItem value="en-US">English (American)</SelectItem>
                          <SelectItem value="es">Spanish</SelectItem>
                          <SelectItem value="fr">French</SelectItem>
                          <SelectItem value="de">German</SelectItem>
                        </SelectContent>
                      </Select>
                    </div>
                    <div className="pt-4 border-t border-chat-border">
                      <Label className="text-muted-foreground">Agent Slug</Label>
                      <p className="text-sm font-mono text-foreground mt-1">{selectedAgent.slug}</p>
                      <p className="text-xs text-muted-foreground mt-1">
                        Use this in API calls: <code className="bg-muted px-1 rounded">?agent={selectedAgent.slug}</code>
                      </p>
                    </div>
                  </CardContent>
                </Card>
              </TabsContent>
            </Tabs>
          </div>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <p className="text-muted-foreground">Select an agent or create a new one</p>
          </div>
        )}
      </main>
    </div>
  );
}
