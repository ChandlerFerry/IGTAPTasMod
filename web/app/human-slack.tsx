"use client";

import { useEffect, useMemo, useState } from "react";
import "./human-slack.css";

// One input of the human version and its slack (`<best>-human.slack.json`, enginesim humanize).
export type SlackInput = { tick: number; end: number; kind: string; value: number; class: string; window: number };
export type SlackReport = {
  course: number; capabilities: string; source: string; human: string;
  time: number; runTicks: number; exact: number; exactBefore: number; inputs: SlackInput[];
};
// The human version's own input ticks (`<best>-human.json`), one per frame column below.
export type HumanTick = { x: number; y: number; held: boolean; press: boolean; release: boolean; dash: boolean; dashJump: number; reset?: boolean };
export type SlackReply = { ok: true; file: string; human: string; slack: SlackReport; ticks: HumanTick[] | null };

// exact: one tick or one value only. window: the input moves or re-tilts a little. band: fixed at the start.
// free: no limit found. The strictest input on a frame picks its colour, so a frame that holds an exact input is red.
const TONE: Record<string, { color: string; short: string; label: string; rank: number }> = {
  exact: { color: "#ff5b6e", short: "exact", label: "exact — only this tick or stick value works", rank: 3 },
  band: { color: "#c77dff", short: "band", label: "band — fixed at run start; a stick range works", rank: 2 },
  free: { color: "#38d996", short: "free", label: "free — no timing or value limit found", rank: 0 },
};
function toneOf(cls: string) {
  if (TONE[cls]) return TONE[cls];
  const windowed = /^window (\d+)$/.exec(cls);
  if (windowed) {
    const n = Number(windowed[1]);
    return { color: n <= 2 ? "#ff8a4c" : n <= 4 ? "#ffbf5b" : "#ffd98a", short: `win ${n}`, label: `window ${n} — works on ${n} consecutive ticks`, rank: 1 };
  }
  return { color: "#8fa8c4", short: cls, label: cls, rank: 0 };
}

function glyphs(k: HumanTick) {
  const out: string[] = [];
  if (k.x < -.001) out.push(`←${Math.abs(k.x) < .999 ? Math.round(Math.abs(k.x) * 100) : ""}`);
  else if (k.x > .001) out.push(`→${Math.abs(k.x) < .999 ? Math.round(Math.abs(k.x) * 100) : ""}`);
  if (k.y > .001) out.push("↑");
  else if (k.y < -.001) out.push("↓");
  if (k.press) out.push("J↓");
  else if (k.held) out.push("J");
  if (k.release) out.push("J↑");
  if (k.dash) out.push("D↓");
  return out;
}

function inputName(e: SlackInput) {
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

// The studio's frame timeline for the route a human plays: the human version's frames, colored per input by how much
// slack it has. Exact inputs are the ones that need practice.
export function HumanSlackTimeline({ routeName, load }: { routeName?: string; load: (name: string) => Promise<SlackReply> }) {
  const [reply, setReply] = useState<SlackReply | null>(null);
  const [missing, setMissing] = useState("");
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    if (!routeName) { setReply(null); setMissing(""); return; }
    let live = true;
    setLoading(true);
    load(routeName)
      .then(r => { if (live) { setReply(r); setMissing(""); } })
      .catch(e => { if (live) { setReply(null); setMissing(e instanceof Error ? e.message : String(e)); } })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [routeName, load]);

  // The strictest input on each frame picks its colour; a frame is also ticked where an input's run covers it.
  const frames = useMemo(() => {
    if (!reply) return [];
    const ticks = reply.ticks ?? [];
    const events = new Map<number, SlackInput[]>();
    for (const e of reply.slack.inputs) {
      for (let t = e.tick; t < Math.max(e.tick + 1, e.end); t++) {
        const list = events.get(t) ?? [];
        list.push(e);
        events.set(t, list);
      }
    }
    return ticks.map((k, tick) => {
      const on = events.get(tick) ?? [];
      const strict = on.slice().sort((a, b) => toneOf(b.class).rank - toneOf(a.class).rank)[0];
      return { tick, k, on, strict };
    });
  }, [reply]);

  if (!routeName) return null;
  const slack = reply?.slack;
  const exact = slack?.inputs.filter(e => e.class === "exact") ?? [];
  return <section className="frameTimeline slackTimeline" aria-label="Human version input slack">
    <div className="frameTimelineHeader">
      <div>
        <strong>Human version · input slack</strong>
        <span>{loading ? "Reading the slack report…" : slack
          ? `${reply!.file} · same time as best (${slack.time.toFixed(2)}s) · play this, not the record`
          : missing || "No human version for this route yet."}</span>
      </div>
      {slack && <div className="frameLegend slackLegend">
        <span><i style={{ background: TONE.exact.color }} />exact · practice</span>
        <span><i style={{ background: "#ffbf5b" }} />window</span>
        <span><i style={{ background: TONE.band.color }} />band</span>
        <span><i style={{ background: TONE.free.color }} />free</span>
      </div>}
    </div>
    {slack && <div className="slackSummary">
      <strong>{slack.inputs.length} inputs</strong>
      <span className={exact.length ? "slackExact" : "slackFine"}>{exact.length === 0 ? "none tick-exact"
        : `${exact.length} exact — practice ticks`}{exact.slice(0, 8).map((e, i) => <b key={i} title={`tick ${e.tick}: ${inputName(e)}`}>{i ? ", " : " "}{e.tick}</b>)}{exact.length > 8 ? ", …" : ""}</span>
      <small>Frame colour = the strictest input on it.</small>
    </div>}
    {slack && <details className="howTo"><summary>How slack is measured</summary><ol>
      <li>Each input is nudged up to 6 ticks either way.</li>
      <li>Nudging stops when the run stops working.</li>
      <li>Every stick value is tested the same way.</li>
      <li>exact = only this tick or stick value works.</li>
      <li>window n = works on n consecutive ticks.</li>
      <li>band = stick range, fixed early. free = no limit found.</li>
    </ol></details>}
    {slack && frames.length > 0 && <div className="frameScroller"><div className="frameTrack">
      {frames.map(({ tick, k, on, strict }) => {
        const tone = strict ? toneOf(strict.class) : null;
        return <div key={tick} className={`frameCell slackCell ${tone ? "slackOn" : ""}`} style={tone ? { background: tone.color, color: "#0b1220" } : undefined}
          title={`Frame ${tick + 1}${on.length ? ` · ${on.slice(0, 2).map(e => inputName(e)).join(" · ")}${on.length > 2 ? " …" : ""}` : " · stick held"}`}>
          <b>{tick + 1}</b>
          <span>{glyphs(k).join(" ") || "·"}</span>
          <small>{tone ? tone.short : ""}</small>
        </div>;
      })}
    </div></div>}
    {slack && reply?.ticks == null && <ul className="slackList">
      {slack.inputs.map((e, i) => <li key={i} style={{ borderLeftColor: toneOf(e.class).color }}>
        <b>tick {e.tick}</b> {inputName(e)} <small title={toneOf(e.class).label}>{toneOf(e.class).short}</small></li>)}
    </ul>}
    {slack && <p className="slackFoot">{slack.human}: {slack.runTicks} physics ticks · exact inputs: {slack.exact} here, {slack.exactBefore} in the best</p>}
  </section>;
}
