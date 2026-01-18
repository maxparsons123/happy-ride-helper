import { useState, useEffect } from "react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Slider } from "@/components/ui/slider";
import { Label } from "@/components/ui/label";
import { Volume2, Square, Play, Copy, Check } from "lucide-react";
import { NavLink } from "@/components/NavLink";

export default function TtsTest() {
  const [text, setText] = useState("Hello, your taxi will arrive in 5 minutes at the front entrance.");
  const [voices, setVoices] = useState<SpeechSynthesisVoice[]>([]);
  const [selectedVoice, setSelectedVoice] = useState<string>("");
  const [rate, setRate] = useState(1);
  const [pitch, setPitch] = useState(1);
  const [isSpeaking, setIsSpeaking] = useState(false);
  const [copied, setCopied] = useState(false);

  // Load available voices
  useEffect(() => {
    const loadVoices = () => {
      const availableVoices = speechSynthesis.getVoices();
      // Filter to English voices and sort by name
      const englishVoices = availableVoices
        .filter(v => v.lang.startsWith("en"))
        .sort((a, b) => a.name.localeCompare(b.name));
      setVoices(englishVoices.length > 0 ? englishVoices : availableVoices);
      
      // Select first voice if none selected
      if (!selectedVoice && availableVoices.length > 0) {
        const defaultVoice = englishVoices.find(v => v.default) || englishVoices[0] || availableVoices[0];
        setSelectedVoice(defaultVoice?.name || "");
      }
    };

    loadVoices();
    speechSynthesis.onvoiceschanged = loadVoices;

    return () => {
      speechSynthesis.cancel();
    };
  }, []);

  const speak = () => {
    if (!text.trim()) return;
    
    speechSynthesis.cancel();
    
    const utterance = new SpeechSynthesisUtterance(text);
    const voice = voices.find(v => v.name === selectedVoice);
    if (voice) utterance.voice = voice;
    utterance.rate = rate;
    utterance.pitch = pitch;
    
    utterance.onstart = () => setIsSpeaking(true);
    utterance.onend = () => setIsSpeaking(false);
    utterance.onerror = () => setIsSpeaking(false);
    
    speechSynthesis.speak(utterance);
  };

  const stop = () => {
    speechSynthesis.cancel();
    setIsSpeaking(false);
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
              <NavLink to="/voice">Voice</NavLink>
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
              Type text and preview how it sounds. Uses browser speech synthesis for instant preview.
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
                  {voices.map((voice) => (
                    <SelectItem key={voice.name} value={voice.name}>
                      {voice.name} ({voice.lang})
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            {/* Rate & Pitch */}
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-2">
                <Label>Speed: {rate.toFixed(1)}x</Label>
                <Slider
                  value={[rate]}
                  onValueChange={([v]) => setRate(v)}
                  min={0.5}
                  max={2}
                  step={0.1}
                />
              </div>
              <div className="space-y-2">
                <Label>Pitch: {pitch.toFixed(1)}</Label>
                <Slider
                  value={[pitch]}
                  onValueChange={([v]) => setPitch(v)}
                  min={0.5}
                  max={2}
                  step={0.1}
                />
              </div>
            </div>

            {/* Controls */}
            <div className="flex gap-3">
              {isSpeaking ? (
                <Button onClick={stop} variant="destructive" className="flex-1">
                  <Square className="h-4 w-4 mr-2" />
                  Stop
                </Button>
              ) : (
                <Button onClick={speak} className="flex-1" disabled={!text.trim()}>
                  <Play className="h-4 w-4 mr-2" />
                  Play
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
