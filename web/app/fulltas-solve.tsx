"use client";

import { useState } from "react";

// The Full-TAS solve as the Studio shows it: the record, what the search is doing, what it found, and errors to copy.
// Everything comes from the solver's "@fulltas" events (enginesim/Cli/FullTas.cs Emit), relayed by FullTasPlugin.

export type FullTasSplit = { label: string; ticks: number };
export type RouteJson = { progress: number; time: number | null; splits: number[] };
export type FullTasEvent = { type: string; at: number } & Record<string, unknown>;
export type FullTasOutcome = { kind: "play" | "judge" | "note"; text: string; error?: boolean; run?: string; finished?: boolean; time?: number | null; saved?: boolean;
  reason?: string | null; reached?: number; of?: number; expected?: number; next?: string | null; droppedFrames?: number; splits?: FullTasSplit[] };
export type FullTasSolveState = { running: boolean; name?: string | null; status: string; error?: string | null; command?: string | null;
  events?: FullTasEvent[]; progress?: FullTasEvent | null; log: string[] };

export const place = (p: string) => /^\d+$/.test(p) ? `Course ${p}` : p.replace(/^bonus/i, "Bonus ");

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

// A goal token in words: start:1 → "Start Course 1", at:7645,6050,230,1500,from=left → "Reach (7645, 6050) from the left".
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

const s2 = (t: number | null | undefined) => t == null ? "—" : `${t.toFixed(2)}s`;
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
// `fold`: consecutive misses of the same kind collapse into one line.
type Item = { key: string; at: number; tone: Tone; title: string; detail?: string; count?: number; fold?: "reach" | "polish" };
type Window = { id: number; kind: string; first: number; limit: number; attempt: number; budget: number; start: number; target: number | null; savedStarts: number; at: number };

function asWindow(e: FullTasEvent): Window {
  return { id: num(e.id) ?? 0, kind: str(e.kind), first: num(e.first) ?? 0, limit: num(e.limit) ?? 0, attempt: num(e.attempt) ?? 0, budget: num(e.budget) ?? 60,
    start: num(e.start) ?? 0, target: num(e.target), savedStarts: num(e.savedStarts) ?? 0, at: e.at };
}

function span(w: Window) { return w.limit - w.first <= 1 ? `goal ${w.limit}` : `goals ${w.first + 1}–${w.limit}`; }

function recordItem(e: FullTasEvent, goals: string[]): Item {
  const prev = e.previous as RouteJson, best = e.best as RouteJson, of = goals.length;
  const how = str(e.how);
  if (e.firstComplete) return { key: `r${e.at}`, at: e.at, tone: "best", title: `First complete route: ${of} goals in ${s2(best.time)}`,
    detail: "Replaying in the game to save it. Polishing starts next." };
  if (best.progress > prev.progress) {
    const reached = best.progress - prev.progress === 1 ? `goal ${best.progress}/${of}` : `goals ${prev.progress + 1}–${best.progress}/${of}`;
    return { key: `r${e.at}`, at: e.at, tone: "best", title: `Reached ${reached}: ${goalName(goals[best.progress - 1])} at ${s2(best.time)}`, detail: how };
  }
  const d = (best.time ?? 0) - (prev.time ?? 0);
  if (e.complete) return { key: `r${e.at}`, at: e.at, tone: "best", title: `New record: ${s2(best.time)} (${delta(d)})`, detail: how };
  return { key: `r${e.at}`, at: e.at, tone: "best", title: `Goal ${best.progress}/${of} now at ${s2(best.time)} (${delta(d)})`, detail: how };
}

// The feed, oldest first; runs of fruitless windows fold into one line.
function feed(events: FullTasEvent[], goals: string[]): Item[] {
  const windows = new Map<number, Window>();
  const items: Item[] = [];
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
    switch (e.type) {
      case "session": {
        const best = e.best as RouteJson;
        push({ key: `s${e.at}`, at: e.at, tone: "info", title: `Solve started at ${best.progress === goals.length ? `record ${s2(best.time)}` : `${best.progress}/${goals.length} goals`}`,
          detail: `${num(e.threads)} threads · ${num(e.minutes)} min · ${num(e.window)}-goal windows · ${str(e.from)}` });
        break;
      }
      case "phase":
        push(e.phase === "reach"
          ? { key: `p${e.at}`, at: e.at, tone: "info", title: "Reach phase", detail: "Searching only the next unreached goal. Polishing starts once every goal is reached." }
          : { key: `p${e.at}`, at: e.at, tone: "info", title: "Polish phase", detail: "Searching each goal window from goal 1 for time saves, over the goals the route reaches. The route changes only when its last goal comes sooner." });
        break;
      case "window": windows.set(num(e.id) ?? 0, asWindow(e)); break;
      case "found": {
        const w = windows.get(num(e.id) ?? 0), t = num(e.time);
        if (!w) break;
        push(w.kind === "polish" && w.target != null
          ? { key: `f${e.at}`, at: e.at, tone: "progress", title: `Faster through ${span(w)}: ${s2(t)} vs ${s2(w.target)} (${delta((t ?? 0) - w.target)})`, detail: "Verifying in the whole run." }
          : { key: `f${e.at}`, at: e.at, tone: "progress", title: `Found goal ${w.limit}: ${goalName(goals[w.limit - 1])} at ${s2(t)}`, detail: "Verifying from the start." });
        break;
      }
      case "windowEnd": {
        const w = windows.get(num(e.id) ?? 0);
        if (!w || (num(e.candidates) ?? 0) > 0) break;
        const secs = Math.round((e.at - w.at) / 1000);
        push(w.kind === "reach"
          ? { key: `m${e.at}`, at: e.at, tone: "miss", fold: "reach", title: `No route to goal ${w.limit} (${w.first === w.limit - 1 ? `from goal ${w.first}` : `redoing ${span({ ...w, limit: w.limit - 1 })}`}, ${secs}s)` }
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
        push({ key: `x${e.at}`, at: e.at, tone: "info", title: `Solve finished: ${best.progress === goals.length ? `best ${s2(best.time)}` : `${best.progress}/${goals.length} goals (${s2(best.time)})`}` });
        break;
      }
    }
  }
  return items;
}

// Sentences joined with one period each: the reason may or may not end in one.
function sentences(...parts: (string | null | undefined)[]) {
  return parts.filter(p => p && p.trim()).map(p => p!.trim().replace(/[.:]?$/, ".")).join(" ") || undefined;
}

function outcomeText(r: FullTasOutcome): { title: string; detail?: string } {
  const next = r.next ? `Next goal: ${goalName(r.next)}` : null;
  if (r.kind === "play") {
    if (r.finished) return { title: `Played ${r.run}: finished in ${s2(r.time)}` };
    if (r.error) return { title: `Playback stopped at goal ${r.reached} of ${r.expected}`, detail: sentences(r.reason, next) };
    return { title: `Played the current best: ${r.reached} of ${r.of} goals in ${s2(r.time)}`, detail: sentences("The route ends here so far", next) };
  }
  if (r.kind === "judge") {
    if (!r.finished) return { title: `Game replay failed at goal ${r.reached} of ${r.of}`, detail: sentences(r.reason, next) };
    return r.saved ? { title: `Game replay: ${s2(r.time)}, saved`, detail: r.reason ?? undefined }
      : { title: `Game replay: ${s2(r.time)}, not saved`, detail: r.reason ?? undefined };
  }
  return { title: r.text };
}

async function copy(text: string) {
  try { await navigator.clipboard.writeText(text); return; } catch { /* not a secure context: fall back */ }
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

// `goals`: the run's current goals; `route`: its best measured against them (the plugin trims splits to the goals that still
// match). A finished solve against other goals is outdated: the panel shows the current goals and hides that solve's results.
export function FullTasSolvePanel({ solve, goals, route, run, lastResult, onPlay, canPlay }: { solve: FullTasSolveState; goals: string[]; route: RouteJson | null;
  run: string; lastResult?: FullTasOutcome | null; onPlay: () => void; canPlay: boolean }) {
  const [dismissed, setDismissed] = useState<string>("");
  const allEvents = solve.events ?? [];
  const sessionGoals = allEvents.find(e => e.type === "session")?.goals as string[] | undefined;
  const outdated = !solve.running && sessionGoals != null && (sessionGoals.length !== goals.length || sessionGoals.some((g, i) => g !== goals[i]));
  const events = outdated ? [] : allEvents;
  const session = events.find(e => e.type === "session");
  const labels = outdated ? goals : sessionGoals ?? goals;
  const of = labels.length;
  const records = events.filter(e => e.type === "record");
  const start = session ? session.best as RouteJson : route ?? undefined;
  const best = (records.length ? records[records.length - 1].best : start) as RouteJson | undefined;
  const complete = best != null && best.progress === of && of > 0;
  const now = Date.now();
  const lastWindowEvent = [...events].reverse().find(e => e.type === "window");
  const window_ = lastWindowEvent ? asWindow(lastWindowEvent) : null;
  const windowOpen = solve.running && window_ != null && !events.some(e => e.type === "windowEnd" && e.id === window_.id);
  const progress = solve.progress && window_ && solve.progress.id === window_.id ? solve.progress : null;
  const phase = [...events].reverse().find(e => e.type === "phase")?.phase;
  const items = feed(events, labels);
  const lastRecord = records[records.length - 1];

  // The newest solve problem worth reporting (a failed playback shows in its own outcome box).
  const problems: { key: string; text: string }[] = [];
  if (solve.error) problems.push({ key: `solve:${solve.error}`, text: solve.error });
  for (const e of events) {
    if (e.type === "gate" && !e.finished) problems.push({ key: `gate:${e.at}`, text: `The game couldn't replay the solver's complete route: ${str(e.reason)}` });
    if (e.type === "note" && e.level === "error") problems.push({ key: `note:${e.at}`, text: str(e.text) });
  }
  const problem = problems.length ? problems[problems.length - 1] : null;

  const report = () => [
    `IGTAP Full-TAS report (${new Date().toISOString()})`,
    `Run: ${run}${problem ? `\nProblem: ${problem.text}` : ""}`,
    `Solve: ${solve.running ? "running" : "idle"} · ${solve.status}`,
    best ? `Best: ${best.progress}/${of} goals, ${s2(best.time)} [${best.splits.map((t, i) => `${labels[i]} ${t.toFixed(2)}`).join(" + ")}]` : "Best: none",
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

  return <div className="ftSolve">
    {problem && problem.key !== dismissed && <div className="ftProblem">
      <div><strong>Error</strong><span>{problem.text}</span></div>
      <div className="ftProblemButtons"><CopyButton text={report}/><button className="secondary" onClick={() => setDismissed(problem.key)}>Dismiss</button></div>
    </div>}

    {outdated && <p className="ftNotice">The goals changed after the last solve, so its results are hidden. Solve again to search the new goals.</p>}

    <div className="ftStats">
      <div className={`ftStat ${complete ? "good" : ""}`}><p>{complete ? "Record (offline)" : "Route"}</p>
        <strong>{best ? complete ? s2(best.time) : `${best.progress} / ${of}` : "—"}</strong>
        <span>{best && !complete && best.progress > 0 ? `goal ${best.progress} at ${s2(best.time)}` : complete ? "all goals reached" : "no goals reached"}</span></div>
      <div className={`ftStat ${sessionDelta ? "good" : ""}`}><p>This solve</p><strong>{sessionDelta ?? "—"}</strong>
        <span>{lastRecord ? `last record ${ago(now - lastRecord.at)}` : session ? "no record yet" : outdated ? "goals changed" : "not started"}</span></div>
      <div className="ftStat"><p>Phase</p><strong>{!session ? "—" : polishing ? "Polish" : "Reach"}</strong>
        <span>{!session ? "" : polishing ? (best && best.progress < of ? `goals 1-${best.progress} of ${of}` : "whole-run checked") : best ? `next: goal ${best.progress + 1} of ${of}` : ""}</span></div>
      <div className="ftStat"><p>Elapsed</p><strong>{session ? clock((solve.running ? now : events[events.length - 1].at) - session.at) : "—"}</strong>
        <span>{session ? `of ${num(session.minutes)} min` : ""}</span></div>
    </div>

    {of > 0 && <div className="ftGoalBar" aria-label="Goals reached">
      {labels.map((label, i) => {
        const reached = best != null && i < best.progress;
        const active = windowOpen && window_ != null && i >= window_.first && i < window_.limit;
        return <span key={i} className={`${reached ? "reached" : ""} ${active ? "active" : ""}`} title={`${i + 1}. ${goalName(label)} (${label})${reached && best ? ` · ${s2(best.splits[i])}` : ""}`}/>;
      })}
    </div>}

    {windowOpen && window_ && <div className={`ftNow ${window_.kind}`}>
      <p className="eyebrow">CURRENT WINDOW</p>
      {window_.kind === "reach"
        ? <h3>Searching for goal {window_.limit}: {goalName(labels[window_.limit - 1])}</h3>
        : window_.kind === "repair"
          ? <h3>Re-finding {span(window_)} after an earlier change</h3>
          : <h3>Searching for time saves through {span(window_)}</h3>}
      <p className="ftNowDetail">{window_.kind === "reach"
        ? window_.first === window_.limit - 1 ? `Starting from goal ${window_.first} (${s2(window_.start)}).` : `Redoing ${span({ ...window_, limit: window_.limit - 1 })} to set it up differently.`
        : labels.slice(window_.first, window_.limit).map(goalName).join(" → ")}
        {window_.kind === "reach" && window_.attempt > 1 ? ` Round ${window_.attempt}, longer search.` : ""}
        {window_.savedStarts > 0 ? ` Also trying ${window_.savedStarts} starts from saved routes.` : ""}</p>
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
    {!items.length && !solve.running && <p className="ftEmpty">No solve yet.</p>}

    <details className="ftRaw">
      <summary>Solver log ({solve.log.length} lines)</summary>
      <div className="buttonRow"><CopyButton text={report} label="Copy log and details"/></div>
      <pre>{[...solve.log].reverse().join("\n")}</pre>
    </details>
  </div>;
}
