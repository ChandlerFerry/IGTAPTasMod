"use client";

import { useState } from "react";
import { FullTasSolvePanel, goalSpan, liveSeconds, type FullTasOutcome, type FullTasSolveState, type FullTasSplit } from "./fulltas-solve";

type Run = { name: string; section: string; goals: string[]; best: { splits: FullTasSplit[] } | null;
  offline?: { reached: number; splits: FullTasSplit[] } | null };

/**
 * A Full-TAS run's stretches (goal N-1 to goal N of its best, costed from its splits), longest first: pick one to map it, fence
 * it with avoid boxes ("only" that goal on the map) and Improve it alone (/fulltas/solve with focus [N, N], the Full-TAS tab's
 * "Improve selected"). The next goal the best doesn't reach yet is listed but can't be improved (a focus is on reached goals).
 */
export function FullTasTransitions({ runs, solve, lastResult, busy, onMap, onCommand }: { runs: Run[]; solve: FullTasSolveState;
  lastResult?: FullTasOutcome | null; busy: boolean; onMap: (run: string, stretch: number) => void; onCommand: (path: string, body?: unknown) => Promise<boolean> }) {
  const [name, setName] = useState(() => runs.find(r => r.section === "wip")?.name ?? runs[0]?.name ?? "");
  const [goal, setGoal] = useState<number | null>(null);
  const [all, setAll] = useState(false);
  const run = runs.find(r => r.name === name);
  const splits = run?.best?.splits ?? run?.offline?.splits ?? [];
  const stretches = splits.map((s, i) => ({ goal: i + 1, to: s.label, from: i > 0 ? splits[i - 1].label : "the start",
    ticks: s.ticks - (i > 0 ? splits[i - 1].ticks : 0), seconds: liveSeconds(s) - (i > 0 ? liveSeconds(splits[i - 1]) : 0) }));
  // The 15 longest (or all), in goal order.
  const shown = all ? stretches : [...stretches].sort((a, b) => b.ticks - a.ticks).slice(0, 15).sort((a, b) => a.goal - b.goal);
  const next = run && splits.length < run.goals.length ? { goal: splits.length + 1, label: run.goals[splits.length] } : null;
  const picked = stretches.find(s => s.goal === goal);
  return <div className="panel routePicker">
    <div className="panelTitle"><div><p className="eyebrow">FULL-TAS TRANSITIONS</p><h2>Optimize a stretch</h2></div>
      <span className="hint"><select value={name} onChange={e => { setName(e.target.value); setGoal(null); }}>
        {runs.map(r => <option key={r.name} value={r.name}>{r.name} ({r.section})</option>)}</select>
        {" "}<button className="secondary" onClick={() => setAll(!all)}>{all ? "Only the 15 longest" : `All ${stretches.length}`}</button></span></div>
    {next && <p className="hint">Next unreached: {goalSpan([next.goal, next.goal])} {next.label} (the solver's to find first)</p>}
    {picked && <div className="ftFocus on">
      <span><b>{goalSpan([picked.goal, picked.goal])}</b>: {picked.from} → {picked.to}, {picked.ticks} ticks ({picked.seconds.toFixed(2)} s)</span>
      <span className="buttonRow">
        <button className="secondary" onClick={() => onMap(name, picked.goal - 1)} title="This stretch alone on the map: the best's path through it and its avoid boxes; each box drawn there cuts for this stretch only.">Show stretch</button>
        <button className="primary" disabled={busy} title={`Searches only windows starting at ${goalSpan([picked.goal, picked.goal])}, until Stop.`}
          onClick={() => onCommand("/fulltas/solve", { name, focus: [picked.goal, picked.goal] })}>Improve this stretch</button>
        <button className="danger" disabled={!solve.running} onClick={() => onCommand("/fulltas/stop")}>Stop</button>
      </span></div>}
    {/* The solve's progress as the Full-TAS tab shows it: runs, ticks, runs a second, windows, finds, gate checks. */}
    <FullTasSolvePanel solve={solve} goals={run?.goals ?? []} run={solve.name ?? name} lastResult={lastResult} canPlay={false} onPlay={() => {}}
      route={run ? { progress: splits.length, time: splits.length ? liveSeconds(splits[splits.length - 1]) : null, splits: splits.map(liveSeconds) } : null}
      picked={picked ? [picked.goal, picked.goal] : null} onPick={g => setGoal(g)}/>
    <div className="routeList">{shown.map(s => <div className={`routeRow ${s.goal === goal ? "active" : ""}`} key={s.goal}>
      <button className="routeItem" onClick={() => setGoal(s.goal)}><div><strong>{goalSpan([s.goal, s.goal])}: {s.to}</strong>
        <small>from {s.from} · {s.ticks} ticks ({s.seconds.toFixed(2)} s)</small></div></button></div>)}</div>
  </div>;
}
