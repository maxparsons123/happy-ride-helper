import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Volume2, Square, Play, Copy, Check, Loader2 } from "lucide-react";
import { NavLink } from "@/components/NavLink";
import { toast } from "sonner";

// OpenAI TTS voices (same as used in production)
const VOICES = [
  { id: "shimmer", name: "Shimmer", description: "Warm, friendly female" },
  { id: "alloy", name: "Alloy", description: "Neutral, balanced" },
  { id: "echo", name: "Echo", description: "Warm male" },
  { id: "fable", name: "Fable", description: "British, expressive" },
  { id: "onyx", name: "Onyx", description: "Deep, authoritative male" },
  { id: "nova", name: "Nova", description: "Friendly, upbeat female" },
];

export default function TtsTest() {
  const [text, setText] = useState("Hello, your taxi will arrive in 5 minutes at the front entrance.");
  const [selectedVoice, setSelectedVoice] = useState("shimmer");
  const [isPlaying, setIsPlaying] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [copied, setCopied] = useState(false);
  const [audioElement, setAudioElement] = useState<HTMLAudioElement | null>(null);

  const playTts = async () => {
    if (!text.trim()) return;
    
    // Stop any current audio
    if (audioElement) {
      audioElement.pause();
      audioElement.currentTime = 0;
    }
    
    setIsLoading(true);
    
    try {
      const response = await fetch(
        `${import.meta.env.VITE_SUPABASE_URL}/functions/v1/tts-preview`,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "apikey": import.meta.env.VITE_SUPABASE_PUBLISHABLE_KEY,
            "Authorization": `Bearer ${import.meta.env.VITE_SUPABASE_PUBLISHABLE_KEY}`,
          },
          body: JSON.stringify({ text, voice: selectedVoice }),
        }
      );

      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.error || "TTS request failed");
      }

      const audioBlob = await response.blob();
      const audioUrl = URL.createObjectURL(audioBlob);
      const audio = new Audio(audioUrl);
      
      audio.onplay = () => setIsPlaying(true);
      audio.onended = () => {
        setIsPlaying(false);
        URL.revokeObjectURL(audioUrl);
      };
      audio.onerror = () => {
        setIsPlaying(false);
        toast.error("Failed to play audio");
      };
      
      setAudioElement(audio);
      await audio.play();
    } catch (error) {
      console.error("TTS error:", error);
      toast.error(error instanceof Error ? error.message : "Failed to generate speech");
    } finally {
      setIsLoading(false);
    }
  };

  const stop = () => {
    if (audioElement) {
      audioElement.pause();
      audioElement.currentTime = 0;
      setIsPlaying(false);
    }
  };

  const copyApiCall = () => {
    const apiCall = `POST /functions/v1/taxi-dispatch-callback
{
  "call_id": "YOUR_CALL_ID",
  "action": "say",
  "message": ${JSON.stringify(text)},
  "bypass_ai": true
}`;
    navigator.clipboard.writeText(apiCall);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  return (
    <div className="min-h-screen bg-background">
      <header className="border-b border-border bg-card">
        <div className="container mx-auto px-4 py-4">
          <div className="flex items-center gap-6">
            <h1 className="text-xl font-semibold text-foreground">Ada Voice</h1>
            <nav className="flex gap-4">
              <NavLink to="/">Chat</NavLink>
              <NavLink to="/voice-test">Voice</NavLink>
              <NavLink to="/live">Live Calls</NavLink>
              <NavLink to="/tts-test">TTS Test</NavLink>
              <NavLink to="/agents">Agents</NavLink>
            </nav>
          </div>
        </div>
      </header>

      <main className="container mx-auto px-4 py-8 max-w-2xl">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Volume2 className="h-5 w-5" />
              TTS Preview
            </CardTitle>
            <CardDescription>
              Preview text with OpenAI voices (same as production). Test before sending to live calls.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-6">
            {/* Text Input */}
            <div className="space-y-2">
              <Label htmlFor="tts-text">Text to speak</Label>
              <Textarea
                id="tts-text"
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="Enter text to speak..."
                className="min-h-[120px] resize-none"
              />
            </div>

            {/* Voice Selection */}
            <div className="space-y-2">
              <Label>Voice</Label>
              <Select value={selectedVoice} onValueChange={setSelectedVoice}>
                <SelectTrigger>
                  <SelectValue placeholder="Select a voice" />
                </SelectTrigger>
                <SelectContent>
                  {VOICES.map((voice) => (
                    <SelectItem key={voice.id} value={voice.id}>
                      <div className="flex items-center gap-2">
                        <span className="font-medium">{voice.name}</span>
                        <span className="text-muted-foreground text-sm">— {voice.description}</span>
                      </div>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            {/* Controls */}
            <div className="flex gap-3">
              {isPlaying ? (
                <Button onClick={stop} variant="destructive" className="flex-1">
                  <Square className="h-4 w-4 mr-2" />
                  Stop
                </Button>
              ) : (
                <Button onClick={playTts} className="flex-1" disabled={!text.trim() || isLoading}>
                  {isLoading ? (
                    <Loader2 className="h-4 w-4 mr-2 animate-spin" />
                  ) : (
                    <Play className="h-4 w-4 mr-2" />
                  )}
                  {isLoading ? "Generating..." : "Play"}
                </Button>
              )}
              <Button onClick={copyApiCall} variant="outline">
                {copied ? <Check className="h-4 w-4 mr-2" /> : <Copy className="h-4 w-4 mr-2" />}
                {copied ? "Copied!" : "Copy API Call"}
              </Button>
            </div>

            {/* API Example */}
            <div className="rounded-lg bg-muted p-4 space-y-2">
              <Label className="text-xs text-muted-foreground uppercase tracking-wide">
                Direct TTS API (bypass_ai: true)
              </Label>
              <pre className="text-xs overflow-x-auto whitespace-pre-wrap break-all font-mono text-foreground">
{`POST /functions/v1/taxi-dispatch-callback
{
  "call_id": "YOUR_CALL_ID",
  "action": "say",
  "message": ${JSON.stringify(text)},
  "bypass_ai": true
}`}
              </pre>
            </div>
          </CardContent>
        </Card>
      </main>
    </div>
  );
}
