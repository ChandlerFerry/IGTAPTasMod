"use client";

import { useState, type ReactNode } from "react";

/** A goal's arrival: its physics tick (game frames) and the frames the run had stood still in by then (dash freezes, pauses). */
export type FullTasSplit = { label: string; ticks: number; held?: number };
/** Live time: what the run takes at 1x, ticks at 0.02 s plus held frames at 1/280 s (MacroEngine.LiveSeconds). */
export const liveSeconds = (s: FullTasSplit) => s.ticks * 0.02 + (s.held ?? 0) / 280;
export type RouteJson = { progress: number; time: number | null; splits: number[] };
export type FullTasEvent = { type: string; at: number } & Record<string, unknown>;
export type FullTasOutcome = { kind: "play" | "judge" | "note"; text: string; error?: boolean; run?: string; finished?: boolean; time?: number | null; saved?: boolean;
  reason?: string | null; reached?: number; of?: number; expected?: number; next?: string | null; droppedFrames?: number; splits?: FullTasSplit[] };
export type FullTasSolveState = { running: boolean; name?: string | null; status: string; error?: string | null; command?: string | null;
  events?: FullTasEvent[]; progress?: FullTasEvent | null; log: string[]; outlook?: unknown; pulls?: unknown[] };
/** The saved best's route between two Quick Restarts: goals first+1..last. */
export type SegmentRow = { n: number; first: number; last: number; inputs: number; start?: number };

/** What each bonus's reward box gives (its upgradeBox's own reward), shown in place of the bonusN token. The watts ones are the game's
 *  secrets (upgradeBox.isSecret) by their achievement number; each adds +40% global watts (globalUpgradeSet.cashPerLoop). */
const BONUS_NAMES: Record<string, string> = {
  bonus1: "Global watts, secret 4", bonus2: "Global watts, secret 5", bonus3: "Global watts, secret 7", bonus4: "Global watts, secret 1",
  bonus5: "Global watts, secret 6", bonus6: "Global watts, secret 8", bonus7: "Global watts, secret 9", bonus8: "Global watts, secret 11",
  bonus9: "Global watts, secret 12", bonus10: "Global watts, secret 13", bonus11: "Global watts, secret 14", bonus12: "Global watts, secret 15",
  bonus13: "Global watts, secret 16", bonus14: "Global watts, secret 17", bonus15: "Global watts, secret 3", bonus16: "Global watts, free (not a secret)",
  bonus17: "Refill orbs unlock", bonus18: "Clone dust on Course 8", bonus19: "Buy max on Course 10", bonus20: "VMAN speedrun, 1x",
  bonus21: "VMAN speedrun, ∞x", bonus22: "Global watts, secret 2", bonus23: "Block swap on dash",
  unlockjumpdash: "Jump and Dash refresh orbs (The Tree at level 10)" };

export const place = (p: string) => {
  if (/^\d+$/.test(p)) return `Course ${p}`;
  const name = BONUS_NAMES[p.toLowerCase()];
  return name ? `${name} (${p.toLowerCase()})` : p.replace(/^bonus/i, "Bonus ");
};

const UPGRADES: Record<string, string> = { buywj: "Wall Jump", buyds: "Dash", buydj: "Double Jump", buyomni: "Free Omnidash", buyblockswap5: "Block Swap on Dash",
  buyswap: "Swap Blocks", buystatue: "Prestige Statue", buyclonespeed: "Clone Speed" };

function upgradeName(token: string) {
  const t = token.trim().toLowerCase();
  if (UPGRADES[t]) return UPGRADES[t];
  const m = /^buy(reward|clonemult|clone|globalreward|fast|big|maxsize|prestige|free)(\d*)([a-c]?)$/.exec(t);
  if (!m) return token;
  const what: Record<string, string> = { reward: "Cash per Loop", clone: "Clone", clonemult: "Clone Mult", globalreward: "Global Cash", fast: "Fast Clone", big: "Big Clone",
    maxsize: "Max Clone Size", prestige: "Prestige Box", free: "Free Box" };
  return `${what[m[1]]} (Course ${m[2] || "1"}${m[3] ? " " + m[3] : ""})`;
}

export function goalName(label: string | undefined | null): string {
  if (!label) return "?";
  const m = /^(start|finish|buy|reward|at):(.+)$/.exec(label);
  if (!m) return label;
  switch (m[1]) {
    case "start": return `Start ${place(m[2])}`;
    case "finish": return `Finish ${place(m[2])}`;
    case "buy": return "Buy " + m[2].split("+").map(upgradeName).join(" + ");
    case "reward": return `${place(m[2])} reward`;
    default: { const [x, y] = m[2].split(","); const from = /,from=(.+)$/.exec(m[2]); return `Reach (${x}, ${y})${from ? ` from the ${from[1].split("+").join(" or ")}` : ""}`; }
  }
}

/** A run time as minutes:seconds (`2:05.37`), rounded to the hundredth first so 59.999 reads `1:00.00`. */
export const runTime = (t: number) => {
  const c = Math.round(t * 100);
  return `${Math.floor(c / 6000)}:${((c % 6000) / 100).toFixed(2).padStart(5, "0")}`;
};
const s2 = (t: number | null | undefined) => t == null ? "—" : runTime(t);
const delta = (d: number) => `${d < 0 ? "−" : "+"}${Math.abs(d).toFixed(2)}s`;
function ago(ms: number) {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  return `${Math.floor(s / 3600)}h ${Math.floor(s / 60) % 60}m ago`;
}
function clock(ms: number) {
  const s = Math.max(0, Math.round(ms / 1000));
  return s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor(s / 60) % 60).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}
const num = (v: unknown) => typeof v === "number" ? v : null;
const str = (v: unknown) => typeof v === "string" ? v : "";

type Tone = "best" | "progress" | "miss" | "info" | "warn" | "error";
type Item = { key: string; at: number; tone: Tone; title: string; detail?: string; count?: number; fold?: "reach" | "polish" };
type Window = { id: number; kind: string; first: number; limit: number; logged: number; attempt: number; budget: number; start: number; target: number | null; savedStarts: number; piece: string; at: number };

function asWindow(e: FullTasEvent): Window {
  return { id: num(e.id) ?? 0, kind: str(e.kind), first: num(e.first) ?? 0, limit: num(e.limit) ?? 0, logged: num(e.logged) ?? num(e.first) ?? 0, attempt: num(e.attempt) ?? 0, budget: num(e.budget) ?? 60,
    start: num(e.start) ?? 0, target: num(e.target), savedStarts: num(e.savedStarts) ?? 0, piece: str(e.piece), at: e.at };
}

// Goals are 1-based in the engine's events and logs (a count of goals reached); the studio shows them 0-based, as runs/<run>.json
// indexes them: goal n (1-based) shows as n − 1.
function span(w: Window) { return w.limit - w.first <= 1 ? `goal ${w.limit - 1}` : `goals ${w.first}–${w.limit - 1}`; }
/** Where a window starts: after goal `first` (1-based), or the run's start. */
const fromGoal = (first: number) => first > 0 ? `goal ${first - 1}` : "the start";

function segmentGoals(r: { first: number; last: number }) {
  if (r.last <= r.first) return "no goals";
  return r.last - r.first === 1 ? `goal ${r.last - 1}` : `goals ${r.first}–${r.last - 1}`;
}

/** The saved best's reset segments (the plugin's, from its Quick Restarts). */
export function resetSegments(saved: number[][] | undefined): SegmentRow[] {
  return (saved ?? []).map(([first, last, inputs, start], i) => ({ n: i + 1, first, last, inputs, start }));
}

/**
 * A segment's goals with a bar down their left side, as the TAS overlay marks a tech's ticks: the bar is labeled with the
 * segment's number, its tooltip names its goals and whether saves are kept (safe: no buy after it) or reach a later buy,
 * and a strip under the goals marks its closing Quick Restart. Click the bar to pick its
 * goals for Improve (shift-click adds them to the picked goals).
 */
export function SegmentGroup({ r, end, alt, safe, selected, onPick, disabled, children }: { r: SegmentRow; end: boolean; alt: boolean; safe: boolean;
  selected: boolean; onPick: (shift: boolean) => void; disabled: boolean; children: ReactNode }) {
  const empty = r.last <= r.first;
  const restart = end ? <i className="segIcon end" title="Route ends here">⚑︎</i> : <i className="segIcon restart" title="Quick Restart">↻</i>;
  return <div className={`segmentGroup ${safe ? "safe" : "economy"} ${alt ? "alt" : ""} ${selected ? "selected" : ""} ${empty ? "empty" : ""}`}>
    <button type="button" className="segmentBar" disabled={disabled || empty} onClick={e => onPick(e.shiftKey)}
      title={`Segment ${r.n} · ${segmentGoals(r)} · ${safe ? "saves kept" : "saves affect buys"} · ${selected ? "click to clear the picked goals" : "click to improve just these goals; shift-click to add them"}`}>
      {!empty && <span>{r.n}</span>}
    </button>
    <div className="segmentRows">{children}<div className="segmentRestart">{restart}</div></div>
  </div>;
}

/** The icon key for SegmentGroup's restart strip, shown under a segmented goals table. */
export function SegmentLegend() {
  return <div className="segmentLegend">
    <span><i className="segIcon restart">↻</i>Quick Restart</span><span><i className="segIcon end">⚑︎</i>route end</span>
  </div>;
}

/** The goals (1-based, first..last) whose own splits a new best changed; null when none did. */
function changedGoals(prev: RouteJson, best: RouteJson): [number, number] | null {
  let first = 0, last = 0;
  for (let k = 0; k < Math.min(prev.progress, best.progress); k++) {
    const d = (best.splits[k] - (k ? best.splits[k - 1] : 0)) - (prev.splits[k] - (k ? prev.splits[k - 1] : 0));
    if (Math.abs(d) > 1e-4) { if (!first) first = k + 1; last = k + 1; }
  }
  return first ? [first, last] : null;
}

export const goalSpan = ([a, b]: [number, number]) => a === b ? `goal ${a - 1}` : `goals ${a - 1}–${b - 1}`;

/** A --auto log's latest pull (`auto: pull N: <lever> at goal k (<label>), ...`): what Improve is searching now. */
function lastPull(log: string[]): { lever: string; goal: number; label: string } | null {
  for (let i = log.length - 1; i >= 0; i--) {
    const m = /auto: pull \d+: (\w+) at goal (\d+) \((.*?)\), /.exec(log[i]);
    if (m) return { lever: m[1], goal: Number(m[2]), label: m[3] };
  }
  return null;
}

export const LEVERS: Record<string, string> = { window: "window search", lookahead: "look-ahead window", span: "long window", polish: "input polish",
  graft: "graft from a known route", plan: "contact planner", dropreset: "dropping a quick restart", combine: "splicing known routes",
  revive: "reviving a known route", goals: "economy planner" };

function recordItem(e: FullTasEvent, goals: string[]): Item {
  const prev = e.previous as RouteJson, best = e.best as RouteJson, of = goals.length;
  const how = str(e.how);
  // Improve's goals part changed the goal list: its words, e.g. "economy planned: goals 12-40 planned to buy:buyds on tick 9000".
  if (e.goalsEdit) return { key: `r${e.at}`, at: e.at, tone: "best", title: str(e.goalsEdit),
    detail: `New record ${s2(best.time)} · ${best.progress} goals; the game takes this goal list when it saves the record` };
  if (e.firstComplete) return { key: `r${e.at}`, at: e.at, tone: "best", title: `First complete route: ${of} goals in ${s2(best.time)}`,
    detail: "Replaying in the game to save it. Polishing starts next." };
  if (best.progress > prev.progress) {
    const reached = best.progress - prev.progress === 1 ? `goal ${best.progress - 1}/${of - 1}` : `goals ${prev.progress}–${best.progress - 1}/${of - 1}`;
    return { key: `r${e.at}`, at: e.at, tone: "best", title: `Reached ${reached}: ${goalName(goals[best.progress - 1])} at ${s2(best.time)}`, detail: how };
  }
  const d = (best.time ?? 0) - (prev.time ?? 0);
  const changed = e.complete && prev.progress === of ? changedGoals(prev, best) : null;
  if (changed) return { key: `r${e.at}`, at: e.at, tone: "best", title: `Improved ${goalSpan(changed)}: ${delta(d)}`,
    detail: `New record ${s2(best.time)} · ${how}` };
  if (e.complete) return { key: `r${e.at}`, at: e.at, tone: "best", title: `New record: ${s2(best.time)} (${delta(d)})`, detail: how };
  return { key: `r${e.at}`, at: e.at, tone: "best", title: `Goal ${best.progress - 1}/${of - 1} now at ${s2(best.time)} (${delta(d)})`, detail: how };
}

/** Improve's search steps (windows, finds, repairs) are its own business: its feed is the start, the records, the game's
 *  checks and notes. */
const AUTO_QUIET: Record<string, true> = { session: true, phase: true, window: true, found: true, windowEnd: true, dropped: true };

function feed(events: FullTasEvent[], goals: string[]): Item[] {
  const windows = new Map<number, Window>();
  const items: Item[] = [];
  const auto = events.some(e => e.type === "autoStart");
  const records = events.filter(e => e.type === "record").length;
  const push = (item: Item) => {
    const last = items[items.length - 1];
    if (item.fold && last?.fold === item.fold) {
      last.count = (last.count ?? 1) + 1;
      last.at = item.at;
      last.detail = item.title;
      last.title = item.fold === "reach" ? `No route to the next goal: ${last.count} attempts` : `No time saved: ${last.count} windows`;
      return;
    }
    items.push(item);
  };
  for (const e of events) {
    if (auto && AUTO_QUIET[e.type]) continue;
    switch (e.type) {
      case "session": {
        const best = e.best as RouteJson;
        push({ key: `s${e.at}`, at: e.at, tone: "info", title: `Search started at ${best.progress === goals.length ? `record ${s2(best.time)}` : `${best.progress}/${goals.length} goals`}`,
          detail: `${num(e.threads)} threads · ${num(e.minutes)} min · ${num(e.window)}-goal windows · ${str(e.from)}` });
        break;
      }
      case "autoStart": {
        const best = e.best as RouteJson, focus = e.focus as [number, number] | undefined;
        const whole = !focus || (focus[0] <= 1 && focus[1] >= goals.length);
        push({ key: `a${e.at}`, at: e.at, tone: "info", title: `Improve started from ${s2(best.time)}`,
          detail: `${whole ? "Whole run" : `Only ${goalSpan(focus)}`} · ${num(e.threads)} threads` });
        break;
      }
      case "phase":
        push(e.phase === "reach"
          ? { key: `p${e.at}`, at: e.at, tone: "info", title: "Reach phase", detail: "Next unreached goal only." }
          : e.phase === "build"
            ? { key: `p${e.at}`, at: e.at, tone: "info", title: "Build phase", detail: "Only uncovered steps are searched; unpaid buys get an extra lap." }
            : { key: `p${e.at}`, at: e.at, tone: "info", title: "Polish phase", detail: "Every goal is reached: press Stop, then Improve." });
        break;
      case "window": windows.set(num(e.id) ?? 0, asWindow(e)); break;
      case "found": {
        const w = windows.get(num(e.id) ?? 0), t = num(e.time);
        if (!w) break;
        push(w.kind === "piece"
          ? { key: `f${e.at}`, at: e.at, tone: "progress", title: `Found a piece for goal ${w.limit - 1}: ${w.piece}`, detail: "Filed for reuse; checking it in the run." }
          : w.kind === "polish" && w.target != null
            ? { key: `f${e.at}`, at: e.at, tone: "progress", title: `Faster through ${span(w)} in game time: ${s2(t)} vs ${s2(w.target)} (${delta((t ?? 0) - w.target)})`, detail: "Game time is physics ticks, the time Cash comes in: it leaves out dash freezes, which the goals table and the run's time count. Verifying in the whole run." }
            : { key: `f${e.at}`, at: e.at, tone: "progress", title: `Found goal ${w.limit - 1}: ${goalName(goals[w.limit - 1])} at ${s2(t)}`, detail: "Verifying from the start." });
        break;
      }
      case "windowEnd": {
        const w = windows.get(num(e.id) ?? 0);
        if (!w || (num(e.candidates) ?? 0) > 0) break;
        const secs = Math.round((e.at - w.at) / 1000);
        push(w.kind === "piece"
          ? { key: `m${e.at}`, at: e.at, tone: "miss", title: `No piece found for goal ${w.limit - 1}: ${w.piece} (${secs}s)` }
          : w.kind === "reach"
            ? { key: `m${e.at}`, at: e.at, tone: "miss", fold: "reach", title: `No route to goal ${w.limit - 1} (${w.first === w.limit - 1 ? `from ${fromGoal(w.first)}` : w.logged === w.limit - 1 ? `from just before goal ${w.logged - 1}` : `redoing ${span({ ...w, limit: w.limit - 1 })}`}, ${secs}s)` }
            : { key: `m${e.at}`, at: e.at, tone: "miss", fold: "polish", title: `No time saved through ${span(w)} (${secs}s)` });
        break;
      }
      case "record": push(recordItem(e, goals)); break;
      case "dropped": push({ key: `d${e.at}`, at: e.at, tone: "warn", title: str(e.text) }); break;
      case "note": push({ key: `n${e.at}${items.length}`, at: e.at, tone: e.level === "error" ? "error" : e.level === "warn" ? "warn" : "info", title: str(e.text) }); break;
      case "gate": {
        const t = num(e.time), reason = str(e.reason);
        push(!e.finished
          ? { key: `g${e.at}`, at: e.at, tone: "error", title: "Game replay failed: the sim and the game disagree", detail: reason }
          : e.saved
            ? { key: `g${e.at}`, at: e.at, tone: "best", title: `Game replay: ${s2(t)}, saved as record`, detail: reason }
            : { key: `g${e.at}`, at: e.at, tone: "info", title: `Game replay: ${s2(t)}, not saved`, detail: reason });
        break;
      }
      case "done": {
        const best = e.best as RouteJson;
        push(auto
          ? { key: `x${e.at}`, at: e.at, tone: "info", title: records ? `Improve finished: record ${s2(best.time)}` : `No improvement found: the record stays ${s2(best.time)}` }
          : { key: `x${e.at}`, at: e.at, tone: "info", title: `Search finished: ${best.progress === goals.length ? `best ${s2(best.time)}` : `${best.progress}/${goals.length} goals (${s2(best.time)})`}` });
        break;
      }
    }
  }
  return items;
}

function sentences(...parts: (string | null | undefined)[]) {
  return parts.filter(p => p && p.trim()).map(p => p!.trim().replace(/[.:]?$/, ".")).join(" ") || undefined;
}

function outcomeText(r: FullTasOutcome): { title: string; detail?: string } {
  const next = r.next ? `Next goal: ${goalName(r.next)}` : null;
  if (r.kind === "play") {
    if (r.finished) return { title: `Played ${r.run}: finished in ${s2(r.time)}` };
    if (r.error) return { title: `Playback stopped after ${r.reached} of ${r.expected} goals`, detail: sentences(r.reason, next) };
    return { title: `Played the current best: ${r.reached} of ${r.of} goals in ${s2(r.time)}`, detail: sentences("The route ends here so far", next) };
  }
  if (r.kind === "judge") {
    if (!r.finished) return { title: `Game replay failed after ${r.reached} of ${r.of} goals`, detail: sentences(r.reason, next) };
    return r.saved ? { title: `Game replay: ${s2(r.time)}, saved`, detail: r.reason ?? undefined }
      : { title: `Game replay: ${s2(r.time)}, not saved`, detail: r.reason ?? undefined };
  }
  return { title: r.text };
}

async function copy(text: string) {
  try { await navigator.clipboard.writeText(text); return; } catch {  }
  const area = document.createElement("textarea");
  area.value = text;
  document.body.appendChild(area);
  area.select();
  document.execCommand("copy");
  area.remove();
}

function CopyButton({ text, label = "Copy details" }: { text: () => string; label?: string }) {
  const [done, setDone] = useState(false);
  return <button className="secondary copyButton" onClick={async () => { await copy(text()); setDone(true); window.setTimeout(() => setDone(false), 2000); }}>{done ? "Copied" : label}</button>;
}

export function FullTasSolvePanel({ solve, goals, route, run, lastResult, onPlay, canPlay, picked, onPick }: { solve: FullTasSolveState; goals: string[]; route: RouteJson | null;
  run: string; lastResult?: FullTasOutcome | null; onPlay: () => void; canPlay: boolean; picked: [number, number] | null; onPick: (goal: number, shift: boolean) => void }) {
  const [dismissed, setDismissed] = useState<string>("");
  const allEvents = solve.events ?? [];
  const sessionGoals = allEvents.find(e => e.type === "session")?.goals as string[] | undefined;
  const outdated = !solve.running && sessionGoals != null && (sessionGoals.length !== goals.length || sessionGoals.some((g, i) => g !== goals[i]));
  const events = outdated ? [] : allEvents;
  const session = events.find(e => e.type === "session");
  // Improve (--auto) announces the best it really starts from: the saved record it was handed can beat the session's.
  const autoStart = events.find(e => e.type === "autoStart");
  const labels = outdated ? goals : sessionGoals ?? goals;
  const of = labels.length;
  const records = events.filter(e => e.type === "record");
  const start = (autoStart ?? session)?.best as RouteJson | undefined ?? route ?? undefined;
  const best = (records.length ? records[records.length - 1].best : start) as RouteJson | undefined;
  const complete = best != null && best.progress === of && of > 0;
  const now = Date.now();
  const lastWindowEvent = [...events].reverse().find(e => e.type === "window");
  const window_ = lastWindowEvent ? asWindow(lastWindowEvent) : null;
  const windowOpen = !autoStart && solve.running && window_ != null && !events.some(e => e.type === "windowEnd" && e.id === window_.id);
  const progress = solve.progress && window_ && solve.progress.id === window_.id ? solve.progress : null;
  const phase = [...events].reverse().find(e => e.type === "phase")?.phase;
  const items = feed(events, labels);
  const lastRecord = records[records.length - 1];
  const runFocus = autoStart?.focus as [number, number] | undefined;
  const focused = runFocus != null && (runFocus[0] > 1 || runFocus[1] < of) ? runFocus : null;
  const pull = autoStart && solve.running ? lastPull(solve.log) : null;
  const improved = new Set<number>();
  for (const r of records) {
    const range = r.previous && r.best ? changedGoals(r.previous as RouteJson, r.best as RouteJson) : null;
    if (range) for (let k = range[0]; k <= range[1]; k++) improved.add(k);
  }

  const problems: { key: string; text: string }[] = [];
  if (solve.error) problems.push({ key: `solve:${solve.error}`, text: solve.error });
  for (const e of events) {
    if (e.type === "gate" && !e.finished) problems.push({ key: `gate:${e.at}`, text: `Game can't replay the solver's route: ${str(e.reason)}` });
    if (e.type === "note" && e.level === "error") problems.push({ key: `note:${e.at}`, text: str(e.text) });
  }
  const problem = problems.length ? problems[problems.length - 1] : null;

  const report = () => [
    `IGTAP Full-TAS report (${new Date().toISOString()})`,
    `Run: ${run}${problem ? `\nProblem: ${problem.text}` : ""}`,
    `Search: ${solve.running ? "running" : "idle"} · ${solve.status}`,
    best ? `Best: ${best.progress}/${of} goals, ${s2(best.time)} [${best.splits.map((t, i) => `${labels[i]} ${runTime(t)}`).join(" + ")}]` : "Best: none",
    lastResult ? `Last result: ${lastResult.text}` : "",
    solve.command ? `Command: ${solve.command}` : "",
    "", "Events:", ...allEvents.slice(-60).map(e => JSON.stringify(e)),
    solve.progress ? JSON.stringify(solve.progress) : "",
    "", "Log:", ...solve.log.slice(-200),
  ].join("\n");

  const outcome = lastResult ? outcomeText(lastResult) : null;
  const sessionDelta = (() => {
    if (!start || !best) return null;
    if (best.progress > start.progress) return `+${best.progress - start.progress} goal${best.progress - start.progress === 1 ? "" : "s"}`;
    if (best.time != null && start.time != null && best.time < start.time - 1e-4) return delta(best.time - start.time);
    return null;
  })();
  const polishing = phase === "polish" || complete;
  const minutes = num(session?.minutes);
  const recordsText = `${records.length} record${records.length === 1 ? "" : "s"}${lastRecord ? `, last ${ago(now - lastRecord.at)}` : ""}`;

  return <div className="ftSolve">
    {problem && problem.key !== dismissed && <div className="ftProblem">
      <div><strong>Error</strong><span>{problem.text}</span></div>
      <div className="ftProblemButtons"><CopyButton text={report}/><button className="secondary" onClick={() => setDismissed(problem.key)}>Dismiss</button></div>
    </div>}

    {outdated && <p className="ftNotice">Goals changed after the last search: results hidden. Improve again.</p>}

    <div className="ftStats">
      <div className={`ftStat ${complete ? "good" : ""}`}><p>{complete ? "Record (offline)" : "Route"}</p>
        <strong>{best ? complete ? s2(best.time) : `${best.progress} / ${of}` : "—"}</strong>
        <span>{best && !complete && best.progress > 0 ? `goal ${best.progress - 1} at ${s2(best.time)}` : complete ? "all goals reached" : "no goals reached"}</span></div>
      <div className={`ftStat ${sessionDelta ? "good" : ""}`}><p>{autoStart ? "This Improve" : "This search"}</p><strong>{sessionDelta ?? "—"}</strong>
        <span>{records.length ? recordsText : autoStart ? solve.running ? "no improvement yet" : "no improvement found" : session ? "no record yet" : outdated ? "goals changed" : "not started"}</span></div>
      {autoStart
        ? <div className="ftStat"><p>Searching</p><strong>{pull ? `Goal ${pull.goal - 1}` : solve.running ? String(allEvents.filter(e => e.type === "starting").at(-1)?.text ?? "Starting") : "—"}</strong>
          <span>{pull ? `${LEVERS[pull.lever] ?? pull.lever} · ` : ""}{focused ? `only ${goalSpan(focused)}` : "whole run"}</span></div>
        : <div className="ftStat"><p>Phase</p><strong>{!session ? "—" : polishing ? "Polish" : phase === "build" ? "Build" : "Reach"}</strong>
          <span>{!session ? "" : polishing ? (best && best.progress < of ? `goals 0–${best.progress - 1}/${of - 1}` : "whole-run checked") : best ? `next: goal ${best.progress}/${of - 1}` : ""}</span></div>}
      <div className="ftStat"><p>Elapsed</p><strong>{session ? clock((solve.running ? now : events[events.length - 1].at) - session.at) : "—"}</strong>
        <span>{!session ? "" : minutes != null && minutes >= 24 * 60 ? "until Stop" : `of ${minutes} min`}</span></div>
    </div>

    {of > 0 && <div className="ftGoalBar" aria-label="Goals: click to pick, shift-click to extend">
      {labels.map((label, i) => {
        const reached = best != null && i < best.progress;
        const active = pull ? i + 1 >= pull.goal && i + 1 <= pull.goal + 1 : windowOpen && window_ != null && i >= window_.first && i < window_.limit;
        const isPicked = picked != null && i + 1 >= picked[0] && i + 1 <= picked[1];
        return <span key={i} className={`${reached ? "reached" : ""} ${improved.has(i + 1) ? "improved" : ""} ${isPicked ? "picked" : ""} ${active ? "active" : ""}`}
          onClick={e => onPick(i + 1, e.shiftKey)}
          title={`${i}. ${goalName(label)}${reached && best ? ` · ${s2(best.splits[i])}` : ""}${improved.has(i + 1) ? " · improved" : ""} · click to pick, shift-click to extend`}/>;
      })}
    </div>}

    {windowOpen && window_ && <div className={`ftNow ${window_.kind}`}>
      <p className="eyebrow">CURRENT WINDOW</p>
      {window_.kind === "piece"
        ? <h3>Searching a loop piece for goal {window_.limit - 1}: {window_.piece}</h3>
        : window_.kind === "reach"
          ? <h3>Searching for goal {window_.limit - 1}: {goalName(labels[window_.limit - 1])}</h3>
          : window_.kind === "repair"
            ? <h3>Re-finding {span(window_)} after an earlier change</h3>
            : <h3>Searching for time saves through {span(window_)}</h3>}
      <p className="ftNowDetail">{window_.kind === "piece"
        ? `From ${fromGoal(window_.first)} (${s2(window_.start)}).`
        : window_.kind === "reach"
          ? window_.first === window_.limit - 1 ? `From ${fromGoal(window_.first)} (${s2(window_.start)}).`
            : window_.logged === window_.limit - 1 ? `Starts just before goal ${window_.logged - 1}: its dash's trailing ticks also reach goal ${window_.limit - 1}.`
            : `Redoing ${span({ ...window_, limit: window_.limit - 1 })} for a different setup.`
          : ""}
        {(window_.kind === "reach" || window_.kind === "piece") && window_.attempt > 1 ? ` Round ${window_.attempt}, longer search.` : ""}
        {window_.savedStarts > 0 ? ` Trying ${window_.savedStarts} saved-route starts too.` : ""}</p>
      <div className="ftNowFacts">
        {window_.target != null && <span>To beat: <b>{s2(window_.target)}</b></span>}
        <span>Best this window: <b>{progress && num(progress.best) != null ? s2(num(progress.best)) : "—"}</b>
          {progress && num(progress.best) != null && window_.target != null ? <em className={(num(progress.best) ?? 0) < window_.target ? "good" : "bad"}> {delta((num(progress.best) ?? 0) - window_.target)}</em> : null}</span>
        {progress && <span>Runs simulated: <b>{(num(progress.runs) ?? 0).toLocaleString()}</b></span>}
      </div>
      <div className="ftMeter"><i style={{ width: `${Math.min(100, (num(progress?.elapsed) ?? 0) / window_.budget * 100)}%` }}/></div>
      <small className="ftMeterLabel">{Math.round(num(progress?.elapsed) ?? 0)}s of {Math.round(window_.budget)}s for this window</small>
    </div>}

    {outcome && <div className={`ftOutcome ${lastResult?.error ? "bad" : ""}`}>
      <div><strong>{outcome.title}</strong>{outcome.detail && <small>{outcome.detail}</small>}</div>
      {lastResult?.error ? <CopyButton text={report}/> : canPlay && <button className="secondary" onClick={onPlay}>Play again</button>}
    </div>}

    {items.length > 0 && <ol className="ftFeed">
      {[...items].reverse().slice(0, 80).map(item => <li key={item.key} className={item.tone}>
        <div><strong>{item.title}</strong>{item.detail && <small>{item.detail}</small>}</div>
        <time>{ago(now - item.at)}</time>
      </li>)}
    </ol>}
    {!items.length && !solve.running && <p className="ftEmpty">Nothing searched yet: press Improve.</p>}

    <details className="howTo"><summary>How it works</summary><ol>
      <li><b>Improve</b> searches the whole run from its record and picks the stretches itself.</li>
      <li>Pick goals first (click a goal or a segment bar, shift-click to extend) to search only there.</li>
      <li>Every faster run is replayed in the game in turbo and saved when it holds up.</li>
      <li>It runs until Stop and leaves 4 cores to the game.</li>
      <li>Cyan goals in the bar are the ones this Improve made faster.</li>
      <li>A run that misses goals gets <b>Reach missing goals</b> instead.</li>
    </ol></details>

    <details className="ftRaw">
      <summary>Solver log ({solve.log.length} lines)</summary>
      <div className="buttonRow"><CopyButton text={report} label="Copy log and details"/></div>
      <pre>{[...solve.log].reverse().join("\n")}</pre>
    </details>
  </div>;
}
