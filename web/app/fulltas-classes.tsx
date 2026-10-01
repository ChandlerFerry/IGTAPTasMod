"use client";

import { useState } from "react";
import { runTime } from "./fulltas-solve";

/** A Full-TAS run class: the techs its routes may use and how its best is ranked (enginesim Glitches.RunClass). */
export type FullTasClass = "major" | "nmg" | "human-major" | "human-nmg";
/** The human score's weights: seconds added per major span, per nmg span and per spike-grace tick (fulltas.json `human`). */
export type HumanWeights = { major: number; nmg: number; grace: number };
/** One occurrence of an nmg or major tech in a route: its techs.json id and category, and the tick it starts on. */
export type GlitchSpan = { tech: string; category: string; tick: number; physics: number };
/** A route's techs; null when it was never classified. */
type Glitches = { major: number | null; nmg: number | null; graceTicks: number | null; spans: GlitchSpan[] | null };
/** A class's saved best (fulltas-classes.json); every class has a score, but only the human ones rank by it. */
export type ClassBest = Glitches & { routeId: string; name: string; time: number; score: number | null };
/** A complete route a human-class solve judged (`<traces>/fulltas/candidates/<run>/`, by score): what humans pick from. */
export type FullTasCandidate = Glitches & { file: string; class: FullTasClass; score: number; time: number | null; reached: number; goals: number };
/** The 1x queue: candidates played back to back from the run's start; `index` is the one playing now (0-based), `current`
 *  null between two. It stays once done (`index >= of`) so its results do; a stop cuts `of` to the one playing. A result's
 *  `time` is null and `reason` says why when that play didn't finish. */
export type FullTasQueue = { name: string; index: number; of: number; current: string | null;
  results: { file: string; finished: boolean; time: number | null; reached: number; reason: string | null }[] };
export type FullTasClassRun = { name: string; classes: Record<FullTasClass, ClassBest | null>; human: HumanWeights; candidates: FullTasCandidate[] };

const CLASSES: { key: FullTasClass; label: string; rule: string }[] = [
  { key: "major", label: "Major", rule: "Any tech. Ranked by live time." },
  { key: "nmg", label: "NMG", rule: "Glitchless and nmg techs; a major span rejects the route. Ranked by live time." },
  { key: "human-major", label: "Human · major", rule: "Any tech. Ranked by score." },
  { key: "human-nmg", label: "Human · NMG", rule: "Glitchless and nmg techs; a major span rejects the route. Ranked by score." },
];
const LABEL = Object.fromEntries(CLASSES.map(c => [c.key, c.label])) as Record<FullTasClass, string>;
const SPAN_LINES = 30;

/** A score to 0.01 rounded half up from its shortest decimal, as the plugin's route names and the candidate files show it
 *  (16.345 is 16.35 there; toFixed rounds the binary value below it to 16.34). */
function score2(s: number) {
  return (Math.round(s * 100 + 1e-6) / 100).toFixed(2);
}

/** A route's techs for a tooltip: each nmg/major span with its tick, then its spike-grace ticks. */
function spansText(g: Glitches) {
  if (g.spans == null) return "Not classified yet.";
  const lines = g.spans.slice(0, SPAN_LINES).map(s => `${s.tech} (${s.category}) at tick ${s.tick}`);
  if (g.spans.length > SPAN_LINES) lines.push(`… ${g.spans.length - SPAN_LINES} more`);
  if (g.graceTicks) lines.push(`${g.graceTicks} spike-grace tick${g.graceTicks === 1 ? "" : "s"}`);
  return lines.length ? lines.join("\n") : "No nmg or major tech, no spike grace.";
}

/**
 * A run's four class bests (major, nmg, human-major, human-nmg) with Play and Improve for each, the human score's weights,
 * and the human candidates by score: pick some and play them back to back at 1x from the run's start.
 */
export function FullTasClasses({ run, queue, busy, complete, onCommand }: { run: FullTasClassRun; queue: FullTasQueue | null; busy: boolean;
  complete: boolean; onCommand: (path: string, body?: unknown) => Promise<boolean> }) {
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const w = run.human;
  const candidates = run.candidates;
  const chosen = candidates.filter(c => picked.has(c.file));
  const all = candidates.length > 0 && chosen.length === candidates.length;
  const ours = queue?.name === run.name ? queue : null;
  const playingQueue = queue != null && queue.index < queue.of;
  const played = new Map((ours?.results ?? []).map(r => [r.file, r]));
  return <div className="ftClasses">
    <div className="panelTitle"><div><p className="eyebrow">CLASSES</p><h2>Bests by class</h2></div></div>
    <p className="hint">Score = live s + {w.major} × major spans + {w.nmg} × nmg spans + {w.grace} × spike-grace ticks. A run&apos;s own weights: <code>&quot;human&quot;</code> in fulltas.json.</p>
    <div className="resultTable ftClassTable">
      <div className="tableHead"><span>Class</span><span>Time</span><span>Score</span><span>Major</span><span>NMG</span><span>Grace</span><span/></div>
      {CLASSES.map(c => {
        const b = run.classes[c.key];
        return <div key={c.key} className={`tableRow ${b ? "" : "pending"} ${c.key.startsWith("human") ? "human" : ""}`} title={[c.rule, b ? `${b.name}\n${spansText(b)}` : "No route yet."].join("\n")}>
          <span>{c.label}</span><span>{b ? runTime(b.time) : "—"}</span><span>{b?.score == null ? "—" : score2(b.score)}</span>
          <span>{b?.major ?? "—"}</span><span>{b?.nmg ?? "—"}</span><span>{b?.graceTicks ?? "—"}</span>
          <span className="ftRowButtons">
            <button className="secondary" disabled={busy || !b} title={b ? `Plays ${b.name} in real time from a new game.` : "No route of this class yet."} onClick={() => onCommand("/fulltas/play", { name: run.name, class: c.key })}>Play</button>
            <button className="secondary" disabled={busy || !complete} title={complete ? `Searches for a better ${c.label} route until Stop.` : "Reach every goal first."} onClick={() => onCommand("/fulltas/solve", { name: run.name, class: c.key })}>Improve</button>
          </span>
        </div>;
      })}
    </div>
    {queue && <div className="ftQueue">
      <div>
        <strong>{queue.name !== run.name ? `${queue.name}: ` : ""}{playingQueue ? `Playing ${queue.index + 1}/${queue.of} at 1x` : `Played ${queue.results.length} at 1x`}</strong>
        <small>{playingQueue ? `${queue.current ?? "Settling"} · ` : ""}{queue.results.filter(r => r.finished).length} of {queue.results.length} played finished</small>
        <div className="ftMeter"><i style={{ width: `${queue.of ? 100 * queue.results.length / queue.of : 0}%` }}/></div>
      </div>
      {playingQueue && <button className="danger" onClick={() => onCommand("/fulltas/queue/stop")}>Stop</button>}
    </div>}
    <div className="panelTitle ftCandidatesTitle"><div><p className="eyebrow">HUMAN CANDIDATES · {candidates.length}</p><h2>Pick routes to play</h2></div>
      <button className="primary" disabled={busy || playingQueue || !chosen.length} title="Plays each one at 1x from the run's start, one after another, in score order."
        onClick={() => onCommand("/fulltas/queue", { name: run.name, files: chosen.map(c => c.file) })}>Play selected back to back{chosen.length ? ` (${chosen.length})` : ""}</button></div>
    {candidates.length
      ? <div className="resultTable ftCandidates">
        <div className="tableHead"><span><input type="checkbox" checked={all} title="Select all" onChange={() => setPicked(all ? new Set() : new Set(candidates.map(c => c.file)))}/></span>
          <span>Score</span><span>Time</span><span>Class</span><span>Major</span><span>NMG</span><span>Grace</span><span>1x</span></div>
        {candidates.map(c => {
          const r = played.get(c.file);
          const playing = ours?.current === c.file;
          return <label key={c.file} className={`tableRow ${playing ? "playing" : ""} ${picked.has(c.file) ? "picked" : ""}`} title={`${c.file}\n${spansText(c)}`}>
            <span><input type="checkbox" checked={picked.has(c.file)} onChange={() => setPicked(p => { const n = new Set(p); if (!n.delete(c.file)) n.add(c.file); return n; })}/></span>
            <span>{score2(c.score)}</span><span>{c.time == null ? "—" : runTime(c.time)}</span><span>{LABEL[c.class] ?? c.class}</span>
            <span>{c.major ?? "—"}</span><span>{c.nmg ?? "—"}</span><span>{c.graceTicks ?? "—"}</span>
            <span className={playing || !r ? "" : r.finished ? "good" : "bad"} title={r?.reason ?? undefined}>{playing ? "playing" : !r ? "" : r.finished ? `✓ ${r.time == null ? "" : runTime(r.time)}` : `✗ goal ${r.reached}/${c.goals}`}</span>
          </label>;
        })}
      </div>
      : <p className="hint">No candidates yet: Improve with a human class keeps every complete route it judges here.</p>}
  </div>;
}
