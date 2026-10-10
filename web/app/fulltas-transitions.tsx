"use client";

import { useState } from "react";
import { FullTasSolvePanel, goalSpan, liveSeconds, type FullTasGoal, type FullTasIdle, type FullTasOutcome, type FullTasSolveState, type FullTasSplit } from "./fulltas-solve";

type Run = { name: string; section: string; goals: FullTasGoal[]; best: { splits: FullTasSplit[] } | null;
  offline?: { reached: number; splits: FullTasSplit[]; waits?: number[] | null; idles?: FullTasIdle[] | null } | null;
  stretches?: { goal: string; bestTicks: number; runTicks: number }[] };

/**
 * A Full-TAS run's stretches (goal N-1 to goal N of its best, costed from its splits), longest first: pick one to map it, fence
 * it with avoid boxes ("only" that goal on the map) and Improve it alone (/fulltas/solve with focus [N, N], the Full-TAS tab's
 * "Improve selected"). The next goal the best doesn't reach yet is listed but can't be improved (a focus is on reached goals).
 */
export function FullTasTransitions({ runs, solve, lastResult, busy, onMap, onCommand }: { runs: Run[]; solve: FullTasSolveState;
  lastResult?: FullTasOutcome | null; busy: boolean; onMap: (run: string, stretch: string) => void; onCommand: (path: string, body?: unknown) => Promise<boolean> }) {
  const [name, setName] = useState(() => runs.find(r => r.section === "wip")?.name ?? runs[0]?.name ?? "");
  const [goal, setGoal] = useState<number | null>(null);
  const [all, setAll] = useState(false);
  const run = runs.find(r => r.name === name);
  const splits = run?.best?.splits ?? run?.offline?.splits ?? [];
  // A wait for Cash isn't the stretch's: a stretch is its movement, the wait the economy planner's. A buy's idle (the player at
  // its box before its Cash, FullTasIdles.cs) splits it at the arrival; without one, its still runs (25+ inputs) are its wait.
  const waits = run?.offline?.waits ?? [];
  const idles = run?.offline?.idles ?? [];
  // A lap (record:N, farm:N) is its course's route, improved on its own (Optimize's Improve), not here.
  const stretches = splits.map((s, i) => {
    const prev: FullTasSplit = i > 0 ? splits[i - 1] : { label: "the start", ticks: 0 };
    const idle = idles.find(d => d.id === run?.goals[i]?.id && d.arrive[0] >= prev.ticks && d.arrive[0] <= s.ticks);
    const arrive: FullTasSplit | null = idle ? { label: s.label, ticks: idle.arrive[0], held: idle.arrive[1] } : null;
    const wait = arrive ? s.ticks - arrive.ticks : waits[i] ?? 0;
    const waitSeconds = arrive ? liveSeconds(s) - liveSeconds(arrive) : wait * 0.02;
    return { goal: i + 1, id: run?.goals[i]?.id ?? "", to: s.label, from: prev.label, wait, waitSeconds, cash: idle ? idle.afford[0] - idle.arrive[0] : null,
      ticks: s.ticks - prev.ticks - wait, seconds: liveSeconds(s) - liveSeconds(prev) - waitSeconds };
  }).filter(s => !/^(record|farm):/.test(run?.goals[s.goal - 1]?.label ?? ""));
  const waited = stretches.filter(s => s.wait > 0);
  // The 15 longest (or all), in goal order.
  const shown = all ? stretches : [...stretches].sort((a, b) => b.ticks - a.ticks).slice(0, 15).sort((a, b) => a.goal - b.goal);
  const next = run && splits.length < run.goals.length ? { goal: splits.length + 1, label: run.goals[splits.length].label } : null;
  const picked = stretches.find(s => s.goal === goal);
  // A stretch's best found by Improve this stretch, by its goal's id.
  const found = (g: number) => run?.stretches?.find(s => s.goal === run.goals[g - 1]?.id);
  // The stretch search's latest word (the engine's `stretch` event): its best against the run's own ticks.
  const stretchBest = solve.name === name ? (solve.events ?? []).filter(e => e.type === "stretch").at(-1) : undefined;
  const stretchBestGoal = stretchBest ? (run?.goals.findIndex(g => g.id === stretchBest.goal) ?? -1) + 1 : 0;
  return <div className="panel routePicker">
    <div className="panelTitle"><div><p className="eyebrow">FULL-TAS TRANSITIONS</p><h2>Optimize a stretch</h2></div>
      <span className="hint"><select value={name} onChange={e => { setName(e.target.value); setGoal(null); }}>
        {runs.map(r => <option key={r.name} value={r.name}>{r.name} ({r.section})</option>)}</select>
        {" "}<button className="secondary" onClick={() => setAll(!all)}>{all ? "Only the 15 longest" : `All ${stretches.length}`}</button></span></div>
    {next && <p className="hint">Next unreached: {goalSpan([next.goal, next.goal])} {next.label} (the solver's to find first)</p>}
    {picked && <div className="ftFocus on">
      <span><b>{goalSpan([picked.goal, picked.goal])}</b>: {picked.from} → {picked.to}, {picked.ticks} ticks ({picked.seconds.toFixed(2)} s)</span>
      <span className="buttonRow">
        <button className="secondary" onClick={() => onMap(name, picked.id)} title="This stretch alone on the map: the best's path through it and its avoid boxes; each box drawn there cuts for this stretch only.">Show stretch</button>
        <button className="primary" disabled={busy} title={`Searches this stretch alone (from the goal before to ${goalSpan([picked.goal, picked.goal])}), judged by when it reaches that goal; the rest of the run is left as it is. Until Stop.`}
          onClick={() => onCommand("/fulltas/solve", { name, stretch: picked.id })}>Improve this stretch</button>
        <button className="danger" disabled={!solve.running} onClick={() => onCommand("/fulltas/stop")}>Stop</button>
        <button className="secondary" disabled={busy || !found(picked.goal)} title="Plays the run's best up to the stretch in turbo, then the stretch's best way at 1x from 3 s before it, to its goal."
          onClick={() => onCommand("/fulltas/play", { name, stretch: picked.id })}>Play stretch best</button>
      </span></div>}
    {stretchBest && <p className="hint">Stretch to {stretchBestGoal > 0 ? goalSpan([stretchBestGoal, stretchBestGoal]) : String(stretchBest.label)}: best <b>{String(stretchBest.bestTicks)}</b> ticks against the run's {String(stretchBest.runTicks)} ({Number(stretchBest.runTicks) - Number(stretchBest.bestTicks)} faster); kept in fulltas/{name}.stretches.json for a whole run's Improve</p>}
    {/* The solve's progress as the Full-TAS tab shows it: runs, ticks, runs a second, windows, finds, gate checks. */}
    <FullTasSolvePanel solve={solve} goals={run?.goals ?? []} run={solve.name ?? name} lastResult={lastResult} canPlay={false} onPlay={() => {}}
      route={run ? { progress: splits.length, time: splits.length ? liveSeconds(splits[splits.length - 1]) : null, splits: splits.map(liveSeconds) } : null}
      picked={picked ? [picked.goal, picked.goal] : null} onPick={g => setGoal(g)}/>
    {waited.length > 0 && <p className="hint">Waits for Cash: {waited.length} buys, {waited.reduce((t, s) => t + s.wait, 0)} ticks
      ({waited.reduce((t, s) => t + s.waitSeconds, 0).toFixed(2)} s), left out of their stretches: the economy planner&apos;s to change, not a stretch&apos;s to optimize.</p>}
    <div className="routeList">{shown.map(s => <div key={s.goal}><div className={`routeRow ${s.goal === goal ? "active" : ""}`}>
      <button className="routeItem" onClick={() => setGoal(s.goal)}><div><strong>{goalSpan([s.goal, s.goal])}: {s.to}</strong>
        <small>from {s.from} · {s.ticks} ticks ({s.seconds.toFixed(2)} s){found(s.goal) && found(s.goal)!.bestTicks < s.ticks
          ? <b> → {found(s.goal)!.bestTicks} ticks ({(found(s.goal)!.bestTicks * 0.02).toFixed(2)} s), {s.ticks - found(s.goal)!.bestTicks} faster</b> : null}</small></div></button></div>
      {s.wait > 0 && <div className="routeItem"><small>↳ wait {s.cash !== null ? `at the box, ${s.cash} ticks for its Cash` : "standing"}: {s.wait} ticks
        ({s.waitSeconds.toFixed(2)} s), the economy planner&apos;s</small></div>}</div>)}</div>
  </div>;
}
