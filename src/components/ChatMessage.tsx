import { Car, User } from "lucide-react";
import { cn } from "@/lib/utils";

interface ChatMessageProps {
  role: "user" | "assistant";
  content: string;
}

export function ChatMessage({ role, content }: ChatMessageProps) {
  const isUser = role === "user";

  return (
    <div
      className={cn(
        "flex gap-3 animate-slide-up",
        isUser ? "flex-row-reverse" : "flex-row"
      )}
    >
      <div
        className={cn(
          "flex h-9 w-9 shrink-0 items-center justify-center rounded-full",
          isUser
            ? "bg-gradient-gold shadow-glow"
            : "bg-secondary border border-chat-border"
        )}
      >
        {isUser ? (
          <User className="h-4 w-4 text-primary-foreground" />
        ) : (
          <Car className="h-4 w-4 text-primary" />
        )}
      </div>
      <div
        className={cn(
          "flex max-w-[75%] flex-col gap-1 rounded-2xl px-4 py-3",
          isUser
            ? "bg-chat-user border border-chat-border rounded-tr-sm"
            : "bg-chat-assistant border border-chat-border rounded-tl-sm"
        )}
      >
        <p className="text-sm leading-relaxed text-foreground">{content}</p>
      </div>
    </div>
  );
}
