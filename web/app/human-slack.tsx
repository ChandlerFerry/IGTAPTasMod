export type SlackInput = { tick: number; end: number; kind: string; value: number; class: string; window: number };
export type SlackReport = {
  course: number; capabilities: string; source: string; human: string;
  time: number; runTicks: number; exact: number; exactBefore: number; inputs: SlackInput[];
};
export type HumanTick = { x: number; y: number; held: boolean; press: boolean; release: boolean; dash: boolean; dashJump: number; reset?: boolean };
export type SlackReply = { ok: true; file: string; human: string; slack: SlackReport; ticks: HumanTick[] | null };

export const TONE: Record<string, { color: string; short: string; label: string; rank: number }> = {
  exact: { color: "#ff5b6e", short: "exact", label: "exact — only this tick or stick value works", rank: 3 },
  band: { color: "#c77dff", short: "band", label: "band — fixed at run start; a stick range works", rank: 2 },
  free: { color: "#38d996", short: "free", label: "free — no timing or value limit found", rank: 0 },
};
export function toneOf(cls: string) {
  if (TONE[cls]) return TONE[cls];
  const windowed = /^window (\d+)$/.exec(cls);
  if (windowed) {
    const n = Number(windowed[1]);
    return { color: n <= 2 ? "#ff8a4c" : n <= 4 ? "#ffbf5b" : "#ffd98a", short: `win ${n}`, label: `window ${n} — works on ${n} consecutive ticks`, rank: 1 };
  }
  return { color: "#8fa8c4", short: cls, label: cls, rank: 0 };
}

export function inputName(e: SlackInput) {
  const ticks = e.end - e.tick;
  const span = (e.kind === "stickX" || e.kind === "stickY") && ticks > 0 && ticks <= 3 ? ` for ${ticks} tick${ticks === 1 ? "" : "s"}` : "";
  switch (e.kind) {
    case "stickX": return (e.value === 1 ? "full right" : e.value === -1 ? "full left" : e.value === 0 ? "x neutral" : `x ${e.value.toFixed(2).replace(/0+$/, "").replace(/\.$/, "")}`) + span;
    case "stickY": return (e.value > 0 ? "up" : e.value < 0 ? "down" : "y neutral") + span;
    case "press": return "press jump";
    case "release": return "release jump";
    default: return "press dash" + (e.value > 0 ? ` (jump on dash frame ${e.value})` : "");
  }
}
