import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { Settings, Bot, Mic, Volume2, MapPin, Cpu, Zap } from "lucide-react";
import { Separator } from "@/components/ui/separator";

interface Agent {
  id: string;
  name: string;
  slug: string;
}

interface LiveCallsSettingsProps {
  // Pipeline settings
  useGeminiPipeline: boolean;
  setUseGeminiPipeline: (value: boolean) => void;
  sttProvider: "groq" | "deepgram";
  setSttProvider: (value: "groq" | "deepgram") => void;
  ttsProvider: "elevenlabs" | "deepgram";
  setTtsProvider: (value: "elevenlabs" | "deepgram") => void;
  
  // Agent settings
  agents: Agent[];
  selectedAgent: string;
  setSelectedAgent: (value: string) => void;
  selectedVoice: string;
  setSelectedVoice: (value: string) => void;
  useSimpleMode: boolean;
  setUseSimpleMode: (value: boolean) => void;
  
  // Feature toggles
  addressVerification: boolean;
  setAddressVerification: (value: boolean) => void;
  useTripResolver: boolean;
  setUseTripResolver: (value: boolean) => void;
  addressTtsSplicing: boolean;
  setAddressTtsSplicing: (value: boolean) => void;
  useUnifiedExtraction: boolean;
  setUseUnifiedExtraction: (value: boolean) => void;
  usePassthroughMode: boolean;
  setUsePassthroughMode: (value: boolean) => void;
  
  // Audio processing
  useRasaAudioProcessing: boolean;
  setUseRasaAudioProcessing: (value: boolean) => void;
}

export function LiveCallsSettings({
  useGeminiPipeline,
  setUseGeminiPipeline,
  sttProvider,
  setSttProvider,
  ttsProvider,
  setTtsProvider,
  agents,
  selectedAgent,
  setSelectedAgent,
  selectedVoice,
  setSelectedVoice,
  useSimpleMode,
  setUseSimpleMode,
  addressVerification,
  setAddressVerification,
  useTripResolver,
  setUseTripResolver,
  addressTtsSplicing,
  setAddressTtsSplicing,
  useUnifiedExtraction,
  setUseUnifiedExtraction,
  usePassthroughMode,
  setUsePassthroughMode,
  useRasaAudioProcessing,
  setUseRasaAudioProcessing,
}: LiveCallsSettingsProps) {
  return (
    <Sheet>
      <SheetTrigger asChild>
        <Button variant="outline" size="sm">
          <Settings className="w-4 h-4 mr-2" />
          Settings
        </Button>
      </SheetTrigger>
      <SheetContent className="w-[400px] sm:w-[540px] overflow-y-auto">
        <SheetHeader>
          <SheetTitle>Live Calls Settings</SheetTitle>
          <SheetDescription>
            Configure AI pipeline, voice, and feature toggles
          </SheetDescription>
        </SheetHeader>

        <div className="mt-6 space-y-6">
          {/* Pipeline Section */}
          <div className="space-y-4">
            <div className="flex items-center gap-2 text-sm font-semibold text-muted-foreground uppercase tracking-wider">
              <Cpu className="w-4 h-4" />
              Pipeline
            </div>
            
            <div className="space-y-4 pl-6">
              {/* Main Pipeline Toggle */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">AI Pipeline</p>
                  <p className="text-xs text-muted-foreground">
                    {useGeminiPipeline ? "Gemini with external STT/TTS" : "OpenAI Realtime API"}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`text-xs ${!useGeminiPipeline ? 'text-primary font-medium' : 'text-muted-foreground'}`}>
                    OpenAI
                  </span>
                  <Switch
                    checked={useGeminiPipeline}
                    onCheckedChange={setUseGeminiPipeline}
                  />
                  <span className={`text-xs ${useGeminiPipeline ? 'text-green-400 font-medium' : 'text-muted-foreground'}`}>
                    Gemini
                  </span>
                </div>
              </div>

              {/* STT Provider (Gemini only) */}
              {useGeminiPipeline && (
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">STT Provider</p>
                    <p className="text-xs text-muted-foreground">Speech-to-text engine</p>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className={`text-xs ${sttProvider === "groq" ? 'text-amber-400 font-medium' : 'text-muted-foreground'}`}>
                      Groq
                    </span>
                    <Switch
                      checked={sttProvider === "deepgram"}
                      onCheckedChange={(checked) => setSttProvider(checked ? "deepgram" : "groq")}
                    />
                    <span className={`text-xs ${sttProvider === "deepgram" ? 'text-cyan-400 font-medium' : 'text-muted-foreground'}`}>
                      Deepgram
                    </span>
                  </div>
                </div>
              )}

              {/* TTS Provider (Gemini only) */}
              {useGeminiPipeline && (
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">TTS Provider</p>
                    <p className="text-xs text-muted-foreground">Text-to-speech engine</p>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className={`text-xs ${ttsProvider === "elevenlabs" ? 'text-purple-400 font-medium' : 'text-muted-foreground'}`}>
                      11Labs
                    </span>
                    <Switch
                      checked={ttsProvider === "deepgram"}
                      onCheckedChange={(checked) => setTtsProvider(checked ? "deepgram" : "elevenlabs")}
                    />
                    <span className={`text-xs ${ttsProvider === "deepgram" ? 'text-cyan-400 font-medium' : 'text-muted-foreground'}`}>
                      Deepgram
                    </span>
                  </div>
                </div>
              )}

              {/* Mode Toggle */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">Processing Mode</p>
                  <p className="text-xs text-muted-foreground">
                    {usePassthroughMode ? "Webhook-driven passthrough" : "Full AI conversation"}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`text-xs ${!usePassthroughMode ? 'text-green-400 font-medium' : 'text-muted-foreground'}`}>
                    Full AI
                  </span>
                  <Switch
                    checked={usePassthroughMode}
                    onCheckedChange={setUsePassthroughMode}
                  />
                  <span className={`text-xs ${usePassthroughMode ? 'text-blue-400 font-medium' : 'text-muted-foreground'}`}>
                    Passthrough
                  </span>
                </div>
              </div>
            </div>
          </div>

          <Separator />

          {/* Voice Section */}
          <div className="space-y-4">
            <div className="flex items-center gap-2 text-sm font-semibold text-muted-foreground uppercase tracking-wider">
              <Mic className="w-4 h-4" />
              Voice & Agent
            </div>
            
            <div className="space-y-4 pl-6">
              {/* Agent Selector */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">Agent</p>
                  <p className="text-xs text-muted-foreground">Select AI personality</p>
                </div>
                <Select value={selectedAgent} onValueChange={setSelectedAgent}>
                  <SelectTrigger className="w-[160px]">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="ada">
                      <div className="flex items-center gap-2">
                        <Bot className="w-3 h-3" />
                        Ada (Default)
                      </div>
                    </SelectItem>
                    {agents.map((agent) => (
                      <SelectItem key={agent.id} value={agent.slug}>
                        <div className="flex items-center gap-2">
                          <Bot className="w-3 h-3" />
                          {agent.name}
                        </div>
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              {/* Voice Selector (OpenAI only) */}
              {!useGeminiPipeline && (
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">Voice</p>
                    <p className="text-xs text-muted-foreground">OpenAI TTS voice</p>
                  </div>
                  <Select value={selectedVoice} onValueChange={setSelectedVoice}>
                    <SelectTrigger className="w-[160px]">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="shimmer">Shimmer</SelectItem>
                      <SelectItem value="alloy">Alloy</SelectItem>
                      <SelectItem value="echo">Echo</SelectItem>
                      <SelectItem value="fable">Fable</SelectItem>
                      <SelectItem value="onyx">Onyx</SelectItem>
                      <SelectItem value="nova">Nova</SelectItem>
                      <SelectItem value="ash">Ash</SelectItem>
                      <SelectItem value="coral">Coral</SelectItem>
                      <SelectItem value="sage">Sage</SelectItem>
                      <SelectItem value="verse">Verse</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              )}

              {/* Simple Mode Toggle */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">Simple Mode</p>
                  <p className="text-xs text-muted-foreground">
                    {useSimpleMode ? "Direct OpenAI + webhook dispatch" : "Full pipeline with extraction"}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`text-xs ${!useSimpleMode ? 'text-primary font-medium' : 'text-muted-foreground'}`}>
                    Full
                  </span>
                  <Switch
                    checked={useSimpleMode}
                    onCheckedChange={setUseSimpleMode}
                  />
                  <span className={`text-xs ${useSimpleMode ? 'text-amber-400 font-medium' : 'text-muted-foreground'}`}>
                    Simple
                  </span>
                </div>
              </div>
            </div>
          </div>

          <Separator />

          {/* Address Features Section */}
          <div className="space-y-4">
            <div className="flex items-center gap-2 text-sm font-semibold text-muted-foreground uppercase tracking-wider">
              <MapPin className="w-4 h-4" />
              Address Features
            </div>
            
            <div className="space-y-4 pl-6">
              {/* Address Verification */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">Verify Addresses</p>
                  <p className="text-xs text-muted-foreground">Geocode pickup & destination</p>
                </div>
                <Switch
                  checked={addressVerification}
                  onCheckedChange={setAddressVerification}
                />
              </div>

              {/* Trip Resolver (only when verification enabled) */}
              {addressVerification && (
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-sm font-medium">Trip Resolver</p>
                    <p className="text-xs text-muted-foreground">AI-enhanced address resolution</p>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className={`text-xs ${!useTripResolver ? 'text-primary font-medium' : 'text-muted-foreground'}`}>
                      Basic
                    </span>
                    <Switch
                      checked={useTripResolver}
                      onCheckedChange={setUseTripResolver}
                    />
                    <span className={`text-xs ${useTripResolver ? 'text-green-400 font-medium' : 'text-muted-foreground'}`}>
                      AI
                    </span>
                  </div>
                </div>
              )}

              {/* Address TTS Splicing */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">Address TTS Splicing</p>
                  <p className="text-xs text-muted-foreground">Pre-recorded address audio</p>
                </div>
                <Switch
                  checked={addressTtsSplicing}
                  onCheckedChange={setAddressTtsSplicing}
                />
              </div>
            </div>
          </div>

          <Separator />

          {/* Audio Processing Section */}
          <div className="space-y-4">
            <div className="flex items-center gap-2 text-sm font-semibold text-muted-foreground uppercase tracking-wider">
              <Volume2 className="w-4 h-4" />
              Audio Processing
            </div>
            
            <div className="space-y-4 pl-6">
              {/* Rasa-Style Audio Processing */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">Rasa-Style Processing</p>
                  <p className="text-xs text-muted-foreground">
                    {useRasaAudioProcessing 
                      ? "μ-law→PCM16, 8→16kHz, interim transcripts" 
                      : "Standard OpenAI audio format"}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`text-xs ${!useRasaAudioProcessing ? 'text-primary font-medium' : 'text-muted-foreground'}`}>
                    Standard
                  </span>
                  <Switch
                    checked={useRasaAudioProcessing}
                    onCheckedChange={setUseRasaAudioProcessing}
                  />
                  <span className={`text-xs ${useRasaAudioProcessing ? 'text-orange-400 font-medium' : 'text-muted-foreground'}`}>
                    Rasa
                  </span>
                </div>
              </div>
            </div>
          </div>

          <Separator />

          {/* Extraction Section */}
          <div className="space-y-4">
            <div className="flex items-center gap-2 text-sm font-semibold text-muted-foreground uppercase tracking-wider">
              <Zap className="w-4 h-4" />
              Data Extraction
            </div>
            
            <div className="space-y-4 pl-6">
              {/* Unified AI Extraction */}
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">AI Extract</p>
                  <p className="text-xs text-muted-foreground">
                    {useUnifiedExtraction ? "Separate AI extraction call" : "Inline extraction from conversation"}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <span className={`text-xs ${!useUnifiedExtraction ? 'text-muted-foreground' : 'text-muted-foreground'}`}>
                    Inline
                  </span>
                  <Switch
                    checked={useUnifiedExtraction}
                    onCheckedChange={setUseUnifiedExtraction}
                  />
                  <span className={`text-xs ${useUnifiedExtraction ? 'text-amber-400 font-medium' : 'text-muted-foreground'}`}>
                    AI Extract
                  </span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </SheetContent>
    </Sheet>
  );
}
