"use client";

import { useState } from "react";
import { runTime } from "./fulltas-solve";

/** A Full-TAS run class: the techs its routes may use and how its best is ranked (enginesim Glitches.RunClass). */
export type FullTasClass = "major" | "nmg" | "human-major" | "human-nmg";
/** The human score's weights: seconds added per major span, per nmg span, per spike-grace tick, and per ln(attempts) of a
 *  whole route's difficulty (fulltas.json `human`). */
export type HumanWeights = { major: number; nmg: number; grace: number; difficulty: number };
/** One occurrence of an nmg or major tech in a route: its techs.json id and category, and the tick it starts on. */
export type GlitchSpan = { tech: string; category: string; tick: number; physics: number };
/** A route's techs; null when it was never classified. */
type Glitches = { major: number | null; nmg: number | null; graceTicks: number | null; spans: GlitchSpan[] | null };
/** A route's difficulty as the human score prices it: `P` the chance one human attempt succeeds, `attempts` 1/P (null at
 *  P 0), `priced` the attempts whose ln the score adds (at P 0, each span no sample works in as one in its samples; absent
 *  from sidecars measured before the score priced difficulty). */
export type Odds = { P: number; attempts: number | null; priced?: number };
/** A class's saved best (fulltas-classes.json); every class has a score, but only the human ones rank by it. A human best's
 *  score includes its difficulty term (`difficulty`, null when not measured). */
export type ClassBest = Glitches & { routeId: string; name: string; time: number; score: number | null; difficulty: Odds | null };
/** One stretch of a route between two anchors (where a human re-syncs: `start`, `land`, `wall`, `freeze` (a dash freeze's
 *  start), `respawn`, `goal`, `finish`, `end`), `from`..`to` in input ticks (`fromPhysics`..`toPhysics` physics ticks):
 *  its input events, the share `p` of its `samples` human-noised plays that still reached the next anchor (no events: no
 *  samples, p 1), the techs and spike-grace ticks in it, and its `chain` (a `chainList` index, null when measured
 *  alone). A bottleneck is p < 0.9. */
export type DifficultySegment = { index: number; from: number; to: number; fromPhysics: number; toPhysics: number; anchor: string; next: string;
  events: number; samples: number; p: number; bottleneck: boolean; chain: number | null; techs: string[]; grace: number };
/** Bottlenecks too close to reset between (gap < `params.chain` ticks) and the segments between them (`segments`, a
 *  contiguous range), measured together: their `p` replaces the product. */
export type DifficultyChain = { index: number; segments: number[]; from: number; to: number; events: number; samples: number; p: number };
/** A candidate's difficulty map (`IGTAP.EngineSim difficulty`, its sidecar in the candidates' `difficulty/` folder): `P`
 *  the chance one human attempt succeeds (the product over chains and lone segments), `attempts` 1/P (null at P 0),
 *  `spacing` the ticks between bottlenecks; `human` when the humanized route was measured (`params.raw` the route as
 *  saved). */
export type Difficulty = Odds & { route: string; finished: boolean; goals: number; live: number; human: boolean;
  params: { samples: number; sigma: number; chain: number; pos: number; vel: number; raw: boolean };
  bottlenecks: number; chains: number; tightest: { segment: number; p: number } | null;
  spacing: number[]; segments: DifficultySegment[]; chainList: DifficultyChain[] };
/** A complete route a human-class solve judged (`<traces>/fulltas/candidates/<run>/`, by score): what humans pick from.
 *  `score` includes `difficultySeconds`, its difficulty term (from its own `odds`, else its sidecar's priced attempts);
 *  null `difficultySeconds`: the score is without the term, a floor. `difficulty` (its map) is null until measured. */
export type FullTasCandidate = Glitches & { file: string; class: FullTasClass; score: number; difficultySeconds: number | null; time: number | null;
  reached: number; goals: number; odds: Odds | null; difficulty: Difficulty | null };
/** The difficulty batch (POST /fulltas/difficulty): `done` of `of` candidates measured (`of` null until the engine counts
 *  them). It stays once it ends or stops. */
export type FullTasDifficultyJob = { name: string; running: boolean; done: number; of: number | null };
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
/** The candidates' order: the run's score (as the plugin lists them), or fewest attempts first (unmeasured last). */
type CandidateOrder = "score" | "attempts";

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

/** A probability as a percentage, to two significant digits below 10 %. */
function percent(p: number) {
  const v = 100 * p;
  return v >= 10 ? v.toFixed(0) : v >= 1 ? v.toFixed(1) : v > 0 ? v.toPrecision(2) : "0";
}

/** The expected attempts until one succeeds (1/P); Infinity when P is 0, whatever the file wrote. */
function attemptsOf(d: Odds) {
  return d.P > 0 ? d.attempts ?? 1 / d.P : Infinity;
}

/** The attempts as shown: one decimal below 10, whole below a million, then an exponent; ∞ at P 0. */
function attemptsText(d: Odds) {
  const a = attemptsOf(d);
  return !isFinite(a) ? "∞" : a < 10 ? a.toFixed(1) : a < 1e6 ? Math.round(a).toString() : a.toExponential(1);
}

/** What a score's difficulty term adds, for a tooltip; a score without it is a floor. */
function termText(seconds: number | null, odds: Odds | null, w: HumanWeights) {
  if (!w.difficulty) return "The run's weights don't price difficulty.";
  if (seconds == null) return "Without its difficulty term (a floor): Measure difficulty adds it.";
  return `Includes ${seconds.toFixed(2)} s of difficulty: ${w.difficulty} × ln ${odds?.priced ?? "?"} attempts${odds && odds.P === 0 ? " (P 0: each span no sample works in as one in its samples)" : ""}.`;
}

/** A difficulty's summary for a tooltip: P, the tightest segment, the spacing and the parameters it was measured with. */
function difficultyText(d: Difficulty) {
  const q = d.params;
  return [`P ${percent(d.P)} % · ${attemptsText(d)} attempts${d.finished ? "" : " · the route doesn't finish"}`,
    d.tightest ? `Tightest: segment ${d.tightest.segment}, p ${percent(d.tightest.p)} %` : "",
    d.spacing.length ? `Bottleneck spacing: ${d.spacing.join(", ")} ticks` : "",
    `${q.samples} samples a segment, σ ${q.sigma} ticks, chain gap ${q.chain} ticks, ±${q.pos} u, ±${percent(q.vel)} % velocity · ${d.human ? "humanized" : "as saved"}`].filter(Boolean).join("\n");
}

/** A segment's tooltip: where it runs, its p and what it uses. */
function segmentText(s: DifficultySegment) {
  return [`Segment ${s.index}: ${s.anchor} → ${s.next} · p ${percent(s.p)} %${s.bottleneck ? " · bottleneck" : ""}${s.chain == null ? "" : ` · chain ${s.chain}`}`,
    `Input ticks ${s.from}–${s.to} (physics ${s.fromPhysics}–${s.toPhysics}) · ${s.events} event${s.events === 1 ? "" : "s"}${s.samples ? `, ${s.samples} samples` : ""}`,
    s.techs.length ? `Techs: ${s.techs.join(", ")}` : "",
    s.grace ? `${s.grace} spike-grace tick${s.grace === 1 ? "" : "s"}` : ""].filter(Boolean).join("\n");
}

/** A segment's shade: clear at p 1, then from a faint amber to a solid red as p falls. */
function shade(p: number) {
  return p >= 1 ? "transparent" : `hsla(${Math.round(40 * p)}, 95%, 58%, ${(0.2 + 0.8 * (1 - p)).toFixed(2)})`;
}

/** A candidate's difficulty along its inputs (input tick 0 to its last segment's end): each segment shaded by its p, the
 *  chains bracketed below, the nmg and major spans marked where they start; hover any for its detail. */
function DifficultyStrip({ d, spans }: { d: Difficulty; spans: GlitchSpan[] | null }) {
  const end = Math.max(1, ...d.segments.map(s => s.to), ...(spans ?? []).map(s => s.tick + 1));
  const at = (tick: number) => `${100 * tick / end}%`;
  return <span className="ftStrip">
    {d.segments.map(s => <i key={s.index} className={`ftSeg ${s.bottleneck ? "neck" : ""}`} style={{ left: at(s.from), width: at(s.to - s.from), background: shade(s.p) }} title={segmentText(s)}/>)}
    {d.chainList.map(c => <i key={c.index} className="ftChain" style={{ left: at(c.from), width: at(c.to - c.from) }}
      title={`Chain ${c.index}: segments ${c.segments.join(", ")} measured together\nInput ticks ${c.from}–${c.to} · ${c.events} events, ${c.samples} samples · p ${percent(c.p)} %`}/>)}
    {spans?.map((s, i) => <i key={i} className={`ftGlitch ${s.category}`} style={{ left: at(s.tick) }} title={`${s.tech} (${s.category}) at tick ${s.tick}`}/>)}
  </span>;
}

/**
 * A run's four class bests (major, nmg, human-major, human-nmg) with Play and Improve for each, the human score's weights,
 * and the human candidates by score or by attempts, each with its difficulty map once measured: pick some and play them
 * back to back at 1x from the run's start.
 */
export function FullTasClasses({ run, queue, difficulty, busy, complete, onCommand }: { run: FullTasClassRun; queue: FullTasQueue | null;
  difficulty: FullTasDifficultyJob | null; busy: boolean; complete: boolean; onCommand: (path: string, body?: unknown) => Promise<boolean> }) {
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [order, setOrder] = useState<CandidateOrder>("score");
  const w = run.human;
  const candidates = run.candidates;
  const chosen = candidates.filter(c => picked.has(c.file));
  const all = candidates.length > 0 && chosen.length === candidates.length;
  const ours = queue?.name === run.name ? queue : null;
  const playingQueue = queue != null && queue.index < queue.of;
  const played = new Map((ours?.results ?? []).map(r => [r.file, r]));
  const measuring = difficulty?.running ?? false;
  const measured = candidates.filter(c => c.difficulty).length;
  // P 0 (Infinity) sorts after every finite count and before the unmeasured; ties keep the score order (the sort is stable).
  const shown = order === "score" ? candidates
    : candidates.slice().sort((a, b) => {
      const da = a.difficulty ?? a.odds, db = b.difficulty ?? b.odds;
      return (da ? attemptsOf(da) : Infinity) - (db ? attemptsOf(db) : Infinity) || +!da - +!db;
    });
  return <div className="ftClasses">
    <div className="panelTitle"><div><p className="eyebrow">CLASSES</p><h2>Bests by class</h2></div></div>
    <p className="hint">Score = live s + {w.major} × major spans + {w.nmg} × nmg spans + {w.grace} × spike-grace ticks + {w.difficulty} × ln(attempts) of the whole route&apos;s difficulty. A run&apos;s own weights: <code>&quot;human&quot;</code> in fulltas.json.</p>
    <div className="resultTable ftClassTable">
      <div className="tableHead"><span>Class</span><span>Time</span><span>Score</span><span>Attempts</span><span>Major</span><span>NMG</span><span>Grace</span><span/></div>
      {CLASSES.map(c => {
        const b = run.classes[c.key];
        const human = c.key.startsWith("human");
        const term = b?.difficulty?.priced == null ? null : w.difficulty * Math.log(b.difficulty.priced);
        return <div key={c.key} className={`tableRow ${b ? "" : "pending"} ${human ? "human" : ""}`} title={[c.rule, b ? `${b.name}\n${spansText(b)}` : "No route yet."].join("\n")}>
          <span>{c.label}</span><span>{b ? runTime(b.time) : "—"}</span>
          <span title={b && human ? termText(term, b.difficulty, w) : undefined}>{b?.score == null ? "—" : `${human && term == null && w.difficulty ? "≥" : ""}${score2(b.score)}`}</span>
          <span>{b?.difficulty ? attemptsText(b.difficulty) : "—"}</span>
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
    {difficulty && <div className="ftQueue ftDifficulty">
      <div>
        <strong>{difficulty.name !== run.name ? `${difficulty.name}: ` : ""}{measuring ? `Measuring difficulty ${difficulty.done}/${difficulty.of ?? "…"}` : `Measured ${difficulty.done}${difficulty.of == null ? "" : ` of ${difficulty.of}`}`}</strong>
        <small>{measuring ? "Every candidate's segments, noised as a human plays them, offline." : "Done or stopped: Measure again skips the ones measured with the same parameters."}</small>
        <div className="ftMeter"><i style={{ width: `${difficulty.of ? 100 * difficulty.done / difficulty.of : 0}%` }}/></div>
      </div>
      {measuring && <button className="danger" onClick={() => onCommand("/fulltas/stop")}>Stop</button>}
    </div>}
    <div className="panelTitle ftCandidatesTitle"><div><p className="eyebrow">HUMAN CANDIDATES · {candidates.length}{measured ? ` · ${measured} MEASURED` : ""}</p><h2>Pick routes to play</h2></div>
      <span className="ftRowButtons">
        <button className="secondary" disabled={measuring || !candidates.length} title="Measures every candidate's difficulty offline: each segment between two anchors, its inputs shifted as a human's would be, and how often it still works."
          onClick={() => onCommand("/fulltas/difficulty", { name: run.name })}>Measure difficulty</button>
        <button className="primary" disabled={busy || playingQueue || !chosen.length} title="Plays each one at 1x from the run's start, one after another, in score order."
          onClick={() => onCommand("/fulltas/queue", { name: run.name, files: chosen.map(c => c.file) })}>Play selected back to back{chosen.length ? ` (${chosen.length})` : ""}</button>
      </span></div>
    {candidates.length
      ? <div className="resultTable ftCandidates">
        <div className="tableHead"><span><input type="checkbox" checked={all} title="Select all" onChange={() => setPicked(all ? new Set() : new Set(candidates.map(c => c.file)))}/></span>
          <span><button className={`ftSort ${order === "score" ? "on" : ""}`} title="Sort by score, best first." onClick={() => setOrder("score")}>Score</button></span>
          <span>Time</span><span>Class</span><span>Major</span><span>NMG</span><span>Grace</span>
          <span title="The chance one human attempt succeeds: the product of every segment's and chain's p.">P %</span>
          <span><button className={`ftSort ${order === "attempts" ? "on" : ""}`} title="Sort by attempts (1/P), fewest first; unmeasured last." onClick={() => setOrder("attempts")}>Attempts</button></span>
          <span title="Bottlenecks: segments under 90 % of human attempts get through.">Necks</span>
          <span title="Chains: bottlenecks too close together to reset between, measured as one.">Chains</span><span>1x</span></div>
        {shown.map(c => {
          const r = played.get(c.file);
          const playing = ours?.current === c.file;
          const d = c.difficulty;
          const odds = d ?? c.odds;
          return <label key={c.file} className={`tableRow ${playing ? "playing" : ""} ${picked.has(c.file) ? "picked" : ""}`} title={`${c.file}\n${spansText(c)}`}>
            <span><input type="checkbox" checked={picked.has(c.file)} onChange={() => setPicked(p => { const n = new Set(p); if (!n.delete(c.file)) n.add(c.file); return n; })}/></span>
            <span title={termText(c.difficultySeconds, c.odds ?? d, w)}>{c.difficultySeconds == null && w.difficulty ? "≥" : ""}{score2(c.score)}</span>
            <span>{c.time == null ? "—" : runTime(c.time)}</span><span>{LABEL[c.class] ?? c.class}</span>
            <span>{c.major ?? "—"}</span><span>{c.nmg ?? "—"}</span><span>{c.graceTicks ?? "—"}</span>
            {d
              ? <><span className={d.finished ? "" : "bad"} title={difficultyText(d)}>{percent(d.P)}</span><span title={difficultyText(d)}>{attemptsText(d)}</span>
                <span title={d.spacing.length ? `Spacing: ${d.spacing.join(", ")} ticks` : undefined}>{d.bottlenecks}</span><span>{d.chains}</span></>
              : odds
                ? <><span title="As the solve measured it; Measure difficulty maps its segments.">{percent(odds.P)}</span><span>{attemptsText(odds)}</span><span>—</span><span>—</span></>
                : <><span>—</span><span>—</span><span>—</span><span>—</span></>}
            <span className={playing || !r ? "" : r.finished ? "good" : "bad"} title={r?.reason ?? undefined}>{playing ? "playing" : !r ? "" : r.finished ? `✓ ${r.time == null ? "" : runTime(r.time)}` : `✗ goal ${r.reached}/${c.goals}`}</span>
            {d && <DifficultyStrip d={d} spans={c.spans}/>}
          </label>;
        })}
      </div>
      : <p className="hint">No candidates yet: Improve with a human class keeps every complete route it judges here.</p>}
  </div>;
}
