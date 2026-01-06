import { Car } from "lucide-react";

export function TypingIndicator() {
  return (
    <div className="flex gap-3 animate-fade-in">
      <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-secondary border border-chat-border">
        <Car className="h-4 w-4 text-primary" />
      </div>
      <div className="flex items-center gap-1 rounded-2xl rounded-tl-sm bg-chat-assistant border border-chat-border px-4 py-3">
        <span
          className="h-2 w-2 rounded-full bg-muted-foreground animate-typing-dot"
          style={{ animationDelay: "0ms" }}
        />
        <span
          className="h-2 w-2 rounded-full bg-muted-foreground animate-typing-dot"
          style={{ animationDelay: "200ms" }}
        />
        <span
          className="h-2 w-2 rounded-full bg-muted-foreground animate-typing-dot"
          style={{ animationDelay: "400ms" }}
        />
      </div>
    </div>
  );
}
