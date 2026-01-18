import { useState, useCallback, useRef, useEffect } from "react";
import {
  Room,
  RoomEvent,
  Track,
  LocalAudioTrack,
  RemoteTrack,
  RemoteTrackPublication,
  Participant,
  ConnectionState,
  createLocalAudioTrack,
} from "livekit-client";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { ScrollArea } from "@/components/ui/scroll-area";
import { supabase } from "@/integrations/supabase/client";
import { toast } from "sonner";
import { 
  Mic, 
  MicOff, 
  Phone, 
  PhoneOff, 
  Settings,
  Volume2,
  Activity,
  Users
} from "lucide-react";
import { Link } from "react-router-dom";

interface TranscriptEntry {
  id: string;
  role: "user" | "assistant" | "system";
  text: string;
  timestamp: Date;
}

export default function LiveKitTest() {
  const [room, setRoom] = useState<Room | null>(null);
  const [connectionState, setConnectionState] = useState<ConnectionState>(ConnectionState.Disconnected);
  const [roomName, setRoomName] = useState(`taxi-test-${Date.now()}`);
  const [participantName, setParticipantName] = useState("web-user");
  const [isMuted, setIsMuted] = useState(false);
  const [transcripts, setTranscripts] = useState<TranscriptEntry[]>([]);
  const [participants, setParticipants] = useState<string[]>([]);
  
  const localAudioTrackRef = useRef<LocalAudioTrack | null>(null);
  const audioElementRef = useRef<HTMLAudioElement | null>(null);

  // Add transcript entry
  const addTranscript = useCallback((role: "user" | "assistant" | "system", text: string) => {
    setTranscripts(prev => [...prev, {
      id: `${Date.now()}-${Math.random()}`,
      role,
      text,
      timestamp: new Date()
    }]);
  }, []);

  // Handle remote track subscription
  const handleTrackSubscribed = useCallback(
    (track: RemoteTrack, publication: RemoteTrackPublication, participant: Participant) => {
      console.log(`Track subscribed: ${track.kind} from ${participant.identity}`);
      
      if (track.kind === Track.Kind.Audio) {
        // Create or reuse audio element for playback
        if (!audioElementRef.current) {
          audioElementRef.current = document.createElement("audio");
          audioElementRef.current.autoplay = true;
          document.body.appendChild(audioElementRef.current);
        }
        track.attach(audioElementRef.current);
        addTranscript("system", `Audio from ${participant.identity} connected`);
      }
    },
    [addTranscript]
  );

  // Handle track unsubscribed
  const handleTrackUnsubscribed = useCallback(
    (track: RemoteTrack, publication: RemoteTrackPublication, participant: Participant) => {
      console.log(`Track unsubscribed: ${track.kind} from ${participant.identity}`);
      track.detach();
    },
    []
  );

  // Connect to LiveKit room
  const connect = useCallback(async () => {
    try {
      addTranscript("system", "Requesting token...");

      // Get token from edge function
      const { data, error } = await supabase.functions.invoke("livekit-token", {
        body: { roomName, participantName }
      });

      if (error || !data?.token) {
        throw new Error(error?.message || "Failed to get token");
      }

      console.log("Got LiveKit token, connecting...", data);
      addTranscript("system", `Connecting to room: ${roomName}`);

      // Create and configure room
      const newRoom = new Room({
        adaptiveStream: true,
        dynacast: true,
        audioCaptureDefaults: {
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
      });

      // Set up event handlers
      newRoom.on(RoomEvent.ConnectionStateChanged, (state) => {
        console.log("Connection state:", state);
        setConnectionState(state);
        if (state === ConnectionState.Connected) {
          addTranscript("system", "Connected to LiveKit room!");
        } else if (state === ConnectionState.Disconnected) {
          addTranscript("system", "Disconnected from room");
        }
      });

      newRoom.on(RoomEvent.TrackSubscribed, handleTrackSubscribed);
      newRoom.on(RoomEvent.TrackUnsubscribed, handleTrackUnsubscribed);

      newRoom.on(RoomEvent.ParticipantConnected, (participant) => {
        console.log("Participant connected:", participant.identity);
        addTranscript("system", `${participant.identity} joined`);
        setParticipants(prev => [...prev, participant.identity]);
      });

      newRoom.on(RoomEvent.ParticipantDisconnected, (participant) => {
        console.log("Participant disconnected:", participant.identity);
        addTranscript("system", `${participant.identity} left`);
        setParticipants(prev => prev.filter(p => p !== participant.identity));
      });

      newRoom.on(RoomEvent.DataReceived, (payload, participant) => {
        const message = new TextDecoder().decode(payload);
        console.log("Data received:", message);
        try {
          const parsed = JSON.parse(message);
          if (parsed.type === "transcript") {
            addTranscript(parsed.role || "assistant", parsed.text);
          }
        } catch {
          addTranscript("assistant", message);
        }
      });

      // Connect to room
      await newRoom.connect(data.url, data.token);
      setRoom(newRoom);

      // Create and publish local audio track
      const audioTrack = await createLocalAudioTrack({
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
      });
      localAudioTrackRef.current = audioTrack;
      await newRoom.localParticipant.publishTrack(audioTrack);
      
      addTranscript("system", "Microphone enabled - you can speak now");
      toast.success("Connected to LiveKit room!");

    } catch (err) {
      console.error("Failed to connect:", err);
      addTranscript("system", `Connection failed: ${err instanceof Error ? err.message : "Unknown error"}`);
      toast.error("Failed to connect to LiveKit");
    }
  }, [roomName, participantName, handleTrackSubscribed, handleTrackUnsubscribed, addTranscript]);

  // Disconnect from room
  const disconnect = useCallback(async () => {
    if (localAudioTrackRef.current) {
      localAudioTrackRef.current.stop();
      localAudioTrackRef.current = null;
    }
    
    if (audioElementRef.current) {
      audioElementRef.current.remove();
      audioElementRef.current = null;
    }
    
    if (room) {
      await room.disconnect();
      setRoom(null);
    }
    
    setParticipants([]);
    addTranscript("system", "Call ended");
    toast.info("Disconnected from room");
  }, [room, addTranscript]);

  // Toggle mute
  const toggleMute = useCallback(async () => {
    if (localAudioTrackRef.current) {
      if (isMuted) {
        await localAudioTrackRef.current.unmute();
        addTranscript("system", "Microphone unmuted");
      } else {
        await localAudioTrackRef.current.mute();
        addTranscript("system", "Microphone muted");
      }
      setIsMuted(!isMuted);
    }
  }, [isMuted, addTranscript]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (room) {
        room.disconnect();
      }
      if (localAudioTrackRef.current) {
        localAudioTrackRef.current.stop();
      }
      if (audioElementRef.current) {
        audioElementRef.current.remove();
      }
    };
  }, [room]);

  const isConnected = connectionState === ConnectionState.Connected;
  const isConnecting = connectionState === ConnectionState.Connecting;

  return (
    <div className="min-h-screen bg-background p-4 md:p-8">
      <div className="max-w-4xl mx-auto space-y-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold">LiveKit Voice Test</h1>
            <p className="text-muted-foreground">
              Test real-time voice communication via LiveKit
            </p>
          </div>
          <div className="flex items-center gap-2">
            <Link to="/live">
              <Button variant="outline" size="sm">
                Back to Live Calls
              </Button>
            </Link>
          </div>
        </div>

        {/* Status Bar */}
        <Card>
          <CardContent className="py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-4">
                <Badge 
                  variant={isConnected ? "default" : isConnecting ? "secondary" : "outline"}
                  className="gap-1"
                >
                  <Activity className="h-3 w-3" />
                  {connectionState}
                </Badge>
                {participants.length > 0 && (
                  <Badge variant="secondary" className="gap-1">
                    <Users className="h-3 w-3" />
                    {participants.length} participant(s)
                  </Badge>
                )}
              </div>
              <div className="flex items-center gap-2">
                <Volume2 className="h-4 w-4 text-muted-foreground" />
                <span className="text-sm text-muted-foreground">
                  Audio: {isConnected ? "Active" : "Inactive"}
                </span>
              </div>
            </div>
          </CardContent>
        </Card>

        <div className="grid md:grid-cols-2 gap-6">
          {/* Connection Controls */}
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Settings className="h-5 w-5" />
                Connection Settings
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-2">
                <Label htmlFor="roomName">Room Name</Label>
                <Input
                  id="roomName"
                  value={roomName}
                  onChange={(e) => setRoomName(e.target.value)}
                  disabled={isConnected || isConnecting}
                  placeholder="Enter room name"
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="participantName">Your Name</Label>
                <Input
                  id="participantName"
                  value={participantName}
                  onChange={(e) => setParticipantName(e.target.value)}
                  disabled={isConnected || isConnecting}
                  placeholder="Enter your name"
                />
              </div>
              
              <div className="flex gap-2 pt-4">
                {!isConnected ? (
                  <Button 
                    onClick={connect} 
                    disabled={isConnecting || !roomName || !participantName}
                    className="flex-1"
                  >
                    <Phone className="h-4 w-4 mr-2" />
                    {isConnecting ? "Connecting..." : "Connect"}
                  </Button>
                ) : (
                  <>
                    <Button 
                      onClick={toggleMute}
                      variant={isMuted ? "destructive" : "secondary"}
                      className="flex-1"
                    >
                      {isMuted ? (
                        <>
                          <MicOff className="h-4 w-4 mr-2" />
                          Unmute
                        </>
                      ) : (
                        <>
                          <Mic className="h-4 w-4 mr-2" />
                          Mute
                        </>
                      )}
                    </Button>
                    <Button 
                      onClick={disconnect}
                      variant="destructive"
                      className="flex-1"
                    >
                      <PhoneOff className="h-4 w-4 mr-2" />
                      Disconnect
                    </Button>
                  </>
                )}
              </div>
            </CardContent>
          </Card>

          {/* Transcript */}
          <Card>
            <CardHeader>
              <CardTitle>Activity Log</CardTitle>
            </CardHeader>
            <CardContent>
              <ScrollArea className="h-[300px]">
                <div className="space-y-2">
                  {transcripts.length === 0 ? (
                    <p className="text-muted-foreground text-sm text-center py-8">
                      No activity yet. Connect to start.
                    </p>
                  ) : (
                    transcripts.map((entry) => (
                      <div 
                        key={entry.id}
                        className={`p-2 rounded text-sm ${
                          entry.role === "user" 
                            ? "bg-primary/10 text-primary" 
                            : entry.role === "assistant"
                            ? "bg-secondary/50"
                            : "bg-muted text-muted-foreground italic"
                        }`}
                      >
                        <span className="font-medium capitalize">{entry.role}: </span>
                        {entry.text}
                        <span className="block text-xs opacity-50 mt-1">
                          {entry.timestamp.toLocaleTimeString()}
                        </span>
                      </div>
                    ))
                  )}
                </div>
              </ScrollArea>
            </CardContent>
          </Card>
        </div>

        {/* Info */}
        <Card>
          <CardContent className="py-4">
            <p className="text-sm text-muted-foreground">
              <strong>Note:</strong> This is a basic LiveKit room connection test. 
              To fully test AI voice, you'll need a LiveKit Agent running on your server 
              that joins the same room and processes audio with OpenAI Realtime API.
            </p>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
