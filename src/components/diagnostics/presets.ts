import type { DiagnosticGroup } from "./types";

/** Default parameter groups for the Ada SIP bridge diagnostic panel */
export const defaultDiagnosticGroups: DiagnosticGroup[] = [
  {
    id: "ingress",
    label: "Signal In (Caller → AI)",
    icon: "📥",
    description:
      "Controls how the caller's audio reaches OpenAI. Boosting ingress gain improves STT accuracy for quiet callers but risks clipping.",
    params: [
      {
        key: "ingressVolumeBoost",
        label: "Ingress Volume Boost",
        description:
          "Multiplier applied to the caller's raw G.711 audio before it reaches OpenAI. Higher values help quiet voices get detected by VAD but can clip loud callers. Recommended: 2.0–6.0×.",
        type: "slider",
        min: 1,
        max: 10,
        step: 0.5,
        unit: "×",
        defaultValue: 4.0,
      },
      {
        key: "bargeInRmsThreshold",
        label: "Barge-In RMS Threshold",
        description:
          "During bot speech, caller audio below this RMS level is replaced with silence to prevent echo feedback. Lower = more sensitive (quieter speech passes through). Higher = more noise rejection. Applies to the raw signal before ingress boost.",
        type: "slider",
        min: 200,
        max: 3000,
        step: 50,
        unit: "RMS",
        defaultValue: 1500,
      },
      {
        key: "echoGuardMs",
        label: "Echo Guard Duration",
        description:
          "After bot speech ends, incoming audio is still gated for this duration to prevent late echo. Too high and the caller's immediate response is clipped. Too low and echo bleeds through.",
        type: "slider",
        min: 50,
        max: 500,
        step: 10,
        unit: "ms",
        defaultValue: 200,
      },
    ],
  },
  {
    id: "egress",
    label: "Signal Out (AI → Caller)",
    icon: "📤",
    description:
      "Controls the AI's audio output to the phone line. Keep egress gain at unity (1.0×) to avoid distortion on telephony codecs.",
    params: [
      {
        key: "egressVolumeBoost",
        label: "Egress Volume Boost",
        description:
          "Multiplier applied to the AI's synthesised audio before it's sent as G.711 to the caller. Keep at 1.0× to avoid A-law clipping artifacts. Only increase if callers report Ada being too quiet.",
        type: "slider",
        min: 0.5,
        max: 3,
        step: 0.1,
        unit: "×",
        defaultValue: 1.0,
      },
      {
        key: "jitterBufferMs",
        label: "Jitter Buffer Size",
        description:
          "Size of the RTP playout jitter buffer. Larger = smoother but more latency. The initial fill requires a full buffer; resumes after underrun need only the rebuffer threshold.",
        type: "slider",
        min: 40,
        max: 500,
        step: 20,
        unit: "ms",
        defaultValue: 200,
      },
    ],
  },
  {
    id: "vad",
    label: "Voice Activity Detection",
    icon: "🎙️",
    description:
      "OpenAI's server-side VAD settings. These control when the AI considers the caller to be speaking or silent, directly affecting turn-taking behavior.",
    params: [
      {
        key: "vadThreshold",
        label: "VAD Threshold",
        description:
          "Sensitivity of OpenAI's voice activity detector. Lower = more sensitive (detects quieter speech). Range: 0.0–1.0. Default 0.3 is tuned for telephony noise floors. Go lower (0.2) for very quiet callers.",
        type: "slider",
        min: 0.05,
        max: 1.0,
        step: 0.05,
        unit: "",
        defaultValue: 0.3,
      },
      {
        key: "vadSilenceDurationMs",
        label: "Silence Duration",
        description:
          "How long the caller must be silent before OpenAI considers the turn complete. Shorter = faster responses but may cut off pauses mid-sentence. Longer = more natural pauses but slower turn-taking.",
        type: "slider",
        min: 200,
        max: 2000,
        step: 50,
        unit: "ms",
        defaultValue: 900,
      },
      {
        key: "vadPrefixPaddingMs",
        label: "Prefix Padding",
        description:
          "Lead-in audio buffer captured before the VAD trigger point. Ensures the start of speech (plosives, short words like 'three') isn't clipped. Higher = safer but may include more background noise.",
        type: "slider",
        min: 100,
        max: 1000,
        step: 50,
        unit: "ms",
        defaultValue: 600,
      },
    ],
  },
  {
    id: "ai",
    label: "AI Model",
    icon: "🤖",
    description:
      "OpenAI Realtime API session parameters. Changes here affect the AI's conversational style and responsiveness.",
    params: [
      {
        key: "temperature",
        label: "Temperature",
        description:
          "Controls randomness of AI responses. Lower = more focused and deterministic (good for dispatch). Higher = more creative but less predictable. Locked at 0.6 for reliable taxi booking.",
        type: "slider",
        min: 0.1,
        max: 1.2,
        step: 0.1,
        unit: "",
        defaultValue: 0.6,
      },
      {
        key: "voice",
        label: "Voice",
        description: "The TTS voice used by the AI. Different voices have different tonal characteristics.",
        type: "select",
        options: [
          { label: "Alloy", value: "alloy" },
          { label: "Echo", value: "echo" },
          { label: "Fable", value: "fable" },
          { label: "Onyx", value: "onyx" },
          { label: "Nova", value: "nova" },
          { label: "Shimmer", value: "shimmer" },
        ],
        defaultValue: "alloy",
      },
      {
        key: "allowInterruptions",
        label: "Allow Interruptions",
        description:
          "When enabled, the caller can interrupt (barge-in) the AI mid-sentence. When disabled, the AI finishes speaking before listening.",
        type: "toggle",
        defaultValue: true,
      },
    ],
  },
  {
    id: "diagnostics",
    label: "Monitoring",
    icon: "📊",
    description: "Controls for the diagnostic logging and monitoring subsystem.",
    params: [
      {
        key: "enableDiagnostics",
        label: "Enable Audio Diagnostics",
        description:
          "Enables periodic RMS / peak / silence% logging on the RTP thread. Disable in production to reduce jitter from logging I/O.",
        type: "toggle",
        defaultValue: true,
      },
      {
        key: "diagnosticIntervalMs",
        label: "Diagnostic Log Interval",
        description: "How often audio quality stats are logged. Lower = more detail but more I/O overhead on the RTP thread.",
        type: "slider",
        min: 1000,
        max: 10000,
        step: 500,
        unit: "ms",
        defaultValue: 5000,
      },
    ],
  },
];
