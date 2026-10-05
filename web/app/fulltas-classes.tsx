"use client";

import { useEffect, useState } from "react";
import { runTime } from "./fulltas-solve";
import { request } from "./studio-socket";

/** A Full-TAS run class: the techs its routes may use and how its best is ranked (enginesim Glitches.RunClass). */
export type FullTasClass = "major" | "nmg" | "human-major" | "human-nmg";
/** The human score's weights: seconds added per spike-grace tick, per frame-perfect input and per forced back-to-back pair
 *  of a whole route's difficulty (fulltas.json `human`); 0 for both of the last two: difficulty isn't scored. */
export type HumanWeights = { grace: number; framePerfect: number; backToBack: number };
/** One occurrence of an nmg or major tech in a route: its techs.json id and category, and the tick it starts on. */
export type GlitchSpan = { tech: string; category: string; tick: number; physics: number };
/** A route's techs; null when it was never classified. */
type Glitches = { major: number | null; nmg: number | null; graceTicks: number | null; spans: GlitchSpan[] | null };
/** A route's difficulty as the human score prices it, counted on humanize's version of the route: its `inputs`, the
 *  `framePerfect` ones (an input that works on one tick only: moved alone a tick earlier or later it breaks the route; a
 *  crossing or start-of-run block counts as a limit too) and the `backToBack` pairs (two actions, inputs on one tick being
 *  one, fewer than 3 ticks apart that no timing of the inputs inside their windows avoids). */
export type DifficultyCounts = { inputs: number; framePerfect: number; backToBack: number };
/** A class's saved best (fulltas-classes.json); every class has a score, but only the human ones rank by it. A human best's
 *  score includes its difficulty's seconds (`difficulty`, null when not measured). */
export type ClassBest = Glitches & { routeId: string; name: string; time: number; score: number | null; difficulty: DifficultyCounts | null };
/** What moving one input a tick alone (earlier or later) does to the route: `fail` it breaks, `blocked` a limit stops the
 *  move, `open` it still works. */
type Shift = "fail" | "blocked" | "open";
/** One input change of humanize's version of a route, at input tick `tick` (`physics`: physics tick): `input` says what it is,
 *  `earliest`..`latest` the ticks it works on (`window` of them), and `framePerfect` when it works on one tick only. */
export type DifficultyEvent = { index: number; tick: number; physics: number; kind: "stick-x" | "stick-y" | "press" | "release" | "dash" | "dash-jump";
  input: string; earliest: number; latest: number; window: number; early: Shift; late: Shift; framePerfect: boolean };
/** Two input changes (`events` indices) fewer than 3 ticks apart as the route plays them (`gap`), and as the timing with the
 *  fewest back-to-back pairs places them (`spread`, 0: one tick): `forced` when that timing still has them back to back. */
export type DifficultyPair = { first: number; second: number; gap: number; spread: number; forced: boolean };
/** A candidate's difficulty map (`IGTAP.EngineSim difficulty`, its sidecar in the candidates' `difficulty/` folder): the
 *  counts, every input change with its window and every close pair; `human` when humanize's version was measured
 *  (`params.raw` the route as saved). */
export type Difficulty = DifficultyCounts & { route: string; finished: boolean; goals: number; live: number; human: boolean;
  params: { radius: number; sync: number; pos: number; vel: number; raw: boolean; measure: string };
  events: DifficultyEvent[]; pairs: DifficultyPair[] };
/** A complete route a human-class solve judged (`<traces>/fulltas/candidates/<run>/`, by score): what humans pick from.
 *  `score` includes `difficultySeconds`, its difficulty's seconds (from its own `difficulty` counts, else its map's); null
 *  `difficultySeconds`: the score is without them, a floor. `difficultyMap` is null until measured. */
export type FullTasCandidate = Glitches & { file: string; class: FullTasClass; score: number; difficultySeconds: number | null; time: number | null;
  reached: number; goals: number; difficulty: DifficultyCounts | null; difficultyMap: Difficulty | null };
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
/** The candidates' order: the run's score (as the plugin lists them), or fewest frame-perfect inputs first, then fewest
 *  back-to-back pairs (unmeasured last). */
type CandidateOrder = "score" | "framePerfect";

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

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

/** Seconds as shown in a tooltip: to 0.01, no trailing zeros. */
const secs = (s: number) => `${+s.toFixed(2)} s`;

/** What a score is made of, for a tooltip: the live time, the spike-grace ticks and the difficulty's counts at the run's
 *  weights; a score without the counts is a floor. */
function scoreText(time: number | null, graceTicks: number | null, d: DifficultyCounts | null, w: HumanWeights) {
  const lines = [time == null ? "" : `${time.toFixed(2)} s live`];
  if (graceTicks) lines.push(`+ ${plural(graceTicks, "spike-grace tick")} × ${w.grace} = ${secs(graceTicks * w.grace)}`);
  if (w.framePerfect === 0 && w.backToBack === 0) lines.push("The run's weights don't price difficulty.");
  else if (!d) lines.push("Difficulty not measured: the score is a floor.");
  else lines.push(`+ ${plural(d.framePerfect, "frame-perfect input")} × ${w.framePerfect} = ${secs(d.framePerfect * w.framePerfect)}`,
    `+ ${plural(d.backToBack, "back-to-back pair")} × ${w.backToBack} = ${secs(d.backToBack * w.backToBack)}`);
  return lines.filter(Boolean).join("\n");
}

/** A heading and its lines for a tooltip, the lines cut at SPAN_LINES. */
function listText(head: string, lines: string[]) {
  const out = [head, ...lines.slice(0, SPAN_LINES)];
  if (lines.length > SPAN_LINES) out.push(`… ${lines.length - SPAN_LINES} more`);
  return out.join("\n");
}

/** A map's close pairs with their two inputs (`events` indices resolved). */
function pairEvents(d: Difficulty) {
  const at = new Map(d.events.map(e => [e.index, e] as const));
  return d.pairs.flatMap(p => { const a = at.get(p.first), b = at.get(p.second); return a && b ? [{ p, a, b }] : []; });
}

/** A route's frame-perfect inputs for a tooltip (its tick and input each); the counts alone until its map is measured. */
function framePerfectText(d: DifficultyCounts, map: Difficulty | null) {
  const head = `${d.framePerfect} frame-perfect of ${plural(d.inputs, "input")}: they work on one tick only${map && !map.finished ? " · the route doesn't finish" : ""}`;
  return map ? listText(head, map.events.filter(e => e.framePerfect).map(e => `tick ${e.tick}: ${e.input}`)) : `${head}\nMeasure difficulty lists them.`;
}

/** A route's forced back-to-back pairs for a tooltip (its ticks and inputs each); the counts alone until its map is measured. */
function pairsText(d: DifficultyCounts, map: Difficulty | null) {
  const head = `${plural(d.backToBack, "forced back-to-back pair")}: under 3 ticks apart however the inputs are timed inside their windows`;
  return map ? listText(head, pairEvents(map).filter(x => x.p.forced).map(({ p, a, b }) => `ticks ${a.tick} and ${b.tick} (${plural(p.gap, "tick")} apart): ${a.input} → ${b.input}`))
    : `${head}\nMeasure difficulty lists them.`;
}

/** A candidate's hard inputs along its input ticks (0 to its last input's or span's): each frame-perfect input a red mark,
 *  each close pair a bracket below (a forced one bright, the rest faint), the nmg and major spans marked where they start;
 *  hover any for its detail. */
function DifficultyStrip({ d, spans }: { d: Difficulty; spans: GlitchSpan[] | null }) {
  const end = Math.max(1, ...d.events.map(e => e.tick + 1), ...(spans ?? []).map(s => s.tick + 1));
  const at = (tick: number) => `${100 * tick / end}%`;
  return <span className="ftStrip">
    {pairEvents(d).map(({ p, a, b }, i) => <i key={`pair${i}`} className={`ftPair ${p.forced ? "forced" : ""}`} style={{ left: at(a.tick), width: at(b.tick - a.tick) }}
      title={`${p.forced ? "Forced back-to-back" : "Close"} pair at ticks ${a.tick} and ${b.tick}: ${plural(p.gap, "tick")} apart\n${a.input} → ${b.input}\n${p.spread === 0 ? "The fewest-pairs timing puts them on one tick." : `The fewest-pairs timing puts them ${plural(p.spread, "tick")} apart${p.forced ? ", under 3" : ""}.`}`}/>)}
    {d.events.filter(e => e.framePerfect).map(e => <i key={`fp${e.index}`} className="ftFp" style={{ left: at(e.tick) }}
      title={`${e.input} at tick ${e.tick} (physics ${e.physics}) · frame-perfect\nWorks on ticks ${e.earliest}–${e.latest} (${plural(e.window, "tick")}) · a tick earlier: ${e.early}, a tick later: ${e.late}`}/>)}
    {spans?.map((s, i) => <i key={i} className={`ftGlitch ${s.category}`} style={{ left: at(s.tick) }} title={`${s.tech} (${s.category}) at tick ${s.tick}`}/>)}
  </span>;
}

/**
 * A run's four class bests (major, nmg, human-major, human-nmg) with Play and Improve for each, the human score's weights,
 * and the human candidates by score or by frame-perfect inputs, each with its hard inputs once measured: pick some and play
 * them back to back at 1x from the run's start.
 */
export function FullTasClasses({ run, queue, difficulty, busy, complete, onCommand }: { run: FullTasClassRun; queue: FullTasQueue | null;
  difficulty: FullTasDifficultyJob | null; busy: boolean; complete: boolean; onCommand: (path: string, body?: unknown) => Promise<boolean> }) {
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [order, setOrder] = useState<CandidateOrder>("score");
  const w = run.human;
  const pricesDifficulty = w.framePerfect !== 0 || w.backToBack !== 0;
  const candidates = run.candidates;
  const chosen = candidates.filter(c => picked.has(c.file));
  const all = candidates.length > 0 && chosen.length === candidates.length;
  const ours = queue?.name === run.name ? queue : null;
  const playingQueue = queue != null && queue.index < queue.of;
  const played = new Map((ours?.results ?? []).map(r => [r.file, r]));
  const measuring = difficulty?.running ?? false;
  const measured = candidates.filter(c => c.difficulty).length;
  // Unmeasured candidates sort after every measured one; ties keep the score order (the sort is stable).
  const shown = order === "score" ? candidates
    : candidates.slice().sort((a, b) => {
      const da = a.difficulty, db = b.difficulty;
      return da && db ? da.framePerfect - db.framePerfect || da.backToBack - db.backToBack : +!da - +!db;
    });
  return <div className="ftClasses">
    <div className="panelTitle"><div><p className="eyebrow">CLASSES</p><h2>Bests by class</h2></div></div>
    <p className="hint">Score = live s + {w.grace} × spike-grace ticks + {w.framePerfect} × frame-perfect inputs + {w.backToBack} × back-to-back pairs (humanize&apos;s version of the route). A run&apos;s own weights: <code>&quot;human&quot;</code> in fulltas.json.</p>
    <div className="resultTable ftClassTable">
      <div className="tableHead"><span>Class</span><span>Time</span><span>Score</span><span title="Inputs that work on one tick only.">Frame-perfect</span><span title="Actions fewer than 3 ticks apart however the inputs are timed inside their windows.">Back-to-back</span><span>Major</span><span>NMG</span><span>Grace</span><span/></div>
      {CLASSES.map(c => {
        const b = run.classes[c.key];
        const human = c.key.startsWith("human");
        const d = b?.difficulty ?? null;
        return <div key={c.key} className={`tableRow ${b ? "" : "pending"} ${human ? "human" : ""}`} title={[c.rule, b ? `${b.name}\n${spansText(b)}` : "No route yet."].join("\n")}>
          <span>{c.label}</span><span>{b ? runTime(b.time) : "—"}</span>
          <span title={b && human ? scoreText(b.time, b.graceTicks, d, w) : undefined}>{b?.score == null ? "—" : `${human && pricesDifficulty && !d ? "≥" : ""}${score2(b.score)}`}</span>
          <span title={d ? `${d.framePerfect} frame-perfect of ${plural(d.inputs, "input")}` : undefined}>{d ? d.framePerfect : "—"}</span>
          <span title={d ? plural(d.backToBack, "forced back-to-back pair") : undefined}>{d ? d.backToBack : "—"}</span>
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
        <small>{measuring ? "Every candidate's inputs, as humanize's version of the route has them, offline." : "Done or stopped: Measure again skips the ones measured with the same parameters."}</small>
        <div className="ftMeter"><i style={{ width: `${difficulty.of ? 100 * difficulty.done / difficulty.of : 0}%` }}/></div>
      </div>
      {measuring && <button className="danger" onClick={() => onCommand("/fulltas/stop")}>Stop</button>}
    </div>}
    <div className="panelTitle ftCandidatesTitle"><div><p className="eyebrow">HUMAN CANDIDATES · {candidates.length}{measured ? ` · ${measured} MEASURED` : ""}</p><h2>Pick routes to play</h2></div>
      <span className="ftRowButtons">
        <button className="secondary" disabled={measuring || !candidates.length} title="Measures every candidate's difficulty offline: each input of humanize's version of the route, the ticks it works on (frame-perfect: one) and the close pairs of inputs that can't be spread apart."
          onClick={() => onCommand("/fulltas/difficulty", { name: run.name })}>Measure difficulty</button>
        <button className="primary" disabled={busy || playingQueue || !chosen.length} title="Plays each one at 1x from the run's start, one after another, in score order."
          onClick={() => onCommand("/fulltas/queue", { name: run.name, files: chosen.map(c => c.file) })}>Play selected back to back{chosen.length ? ` (${chosen.length})` : ""}</button>
      </span></div>
    {candidates.length
      ? <div className="resultTable ftCandidates">
        <div className="tableHead"><span><input type="checkbox" checked={all} title="Select all" onChange={() => setPicked(all ? new Set() : new Set(candidates.map(c => c.file)))}/></span>
          <span><button className={`ftSort ${order === "score" ? "on" : ""}`} title="Sort by score, best first." onClick={() => setOrder("score")}>Score</button></span>
          <span>Time</span><span>Class</span><span>Major</span><span>NMG</span><span>Grace</span>
          <span><button className={`ftSort ${order === "framePerfect" ? "on" : ""}`} title="Frame-perfect inputs work on one tick only. Sort by them, fewest first, then by back-to-back pairs; unmeasured last." onClick={() => setOrder("framePerfect")}>Frame-perfect</button></span>
          <span title="Actions fewer than 3 ticks apart however the inputs are timed inside their windows.">Back-to-back</span><span>1x</span></div>
        {shown.map(c => {
          const r = played.get(c.file);
          const playing = ours?.current === c.file;
          const d = c.difficulty, map = c.difficultyMap;
          return <label key={c.file} className={`tableRow ${playing ? "playing" : ""} ${picked.has(c.file) ? "picked" : ""}`} title={`${c.file}\n${spansText(c)}`}>
            <span><input type="checkbox" checked={picked.has(c.file)} onChange={() => setPicked(p => { const n = new Set(p); if (!n.delete(c.file)) n.add(c.file); return n; })}/></span>
            <span title={scoreText(c.time, c.graceTicks, d, w)}>{c.difficultySeconds == null ? "≥" : ""}{score2(c.score)}</span>
            <span>{c.time == null ? "—" : runTime(c.time)}</span><span>{LABEL[c.class] ?? c.class}</span>
            <span>{c.major ?? "—"}</span><span>{c.nmg ?? "—"}</span><span>{c.graceTicks ?? "—"}</span>
            <span className={map && !map.finished ? "bad" : ""} title={d ? framePerfectText(d, map) : undefined}>{d ? d.framePerfect : "—"}</span>
            <span title={d ? pairsText(d, map) : undefined}>{d ? d.backToBack : "—"}</span>
            <span className={playing || !r ? "" : r.finished ? "good" : "bad"} title={r?.reason ?? undefined}>{playing ? "playing" : !r ? "" : r.finished ? `✓ ${r.time == null ? "" : runTime(r.time)}` : `✗ goal ${r.reached}/${c.goals}`}</span>
            {map && <DifficultyStrip d={map} spans={c.spans}/>}
          </label>;
        })}
      </div>
      : <p className="hint">No candidates yet: Improve with a human class keeps every complete route it judges here.</p>}
  </div>;
}

/** A course route's classes as the engine's `techs-used --json` judges it on the course's start trace (POST
 *  /enginesim/classes): its time (the game timer), spans, spike grace, difficulty, `humanBlock` (what ends its replay as a
 *  human class plays it: a part tilt, a third jump press; null: human inputs) and the human scores (null: not that class). */
type RouteClassesReply = Glitches & { finished: boolean; live: number; humanBlock: string | null; undetected: string[];
  difficulty: DifficultyCounts | null; weights: HumanWeights; score: Record<"human-major" | "human-nmg", number | null> };

/**
 * The Optimize tab's selected course route in the human classes it qualifies for (human-major, human-nmg): its score, and its
 * frame-perfect inputs, back-to-back pairs and spike-grace ticks. Measured offline when the route is selected, kept while its
 * inputs and trace stand.
 */
export function RouteClasses({ route }: { route: { id: string; name: string; actionCount: number } | null }) {
  const [reply, setReply] = useState<{ key: string; data?: RouteClassesReply; error?: string } | null>(null);
  const key = route ? `${route.id}|${route.name}|${route.actionCount}` : "";
  useEffect(() => {
    if (!route) return;
    let alive = true;
    request("/enginesim/classes", { routeId: route.id })
      .then((data: RouteClassesReply) => { if (alive) setReply({ key, data }); })
      .catch((e: Error) => { if (alive) setReply({ key, error: e.message }); });
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  const r = reply?.key === key ? reply : null, d = r?.data;
  const human = CLASSES.filter(c => c.key.startsWith("human") && d?.finished && d.score[c.key as "human-major" | "human-nmg"] != null);
  const dc = d?.difficulty ?? null;
  return <div className="panel ftClasses routeClasses">
    <div className="panelTitle"><div><p className="eyebrow">HUMAN CLASSES</p><h2>Selected route</h2></div><span className="hint">{route?.name ?? "No route selected"}</span></div>
    {!route ? <p className="hint">Select a route above.</p>
      : r?.error ? <p className="hint">{r.error}</p>
      : !d ? <p className="hint">Measuring {route.name} offline…</p>
      : !human.length ? <p className="hint">Not a human-class route.</p>
      : <>
        <p className="hint">Score = time + {d.weights.grace} × spike-grace ticks + {d.weights.framePerfect} × frame-perfect inputs + {d.weights.backToBack} × back-to-back pairs.</p>
        <div className="resultTable rcTable">
          <div className="tableHead"><span>Class</span><span>Time</span><span>Score</span><span title="Inputs that work on one tick only.">Frame-perfect</span><span title="Actions fewer than 3 ticks apart however the inputs are timed inside their windows.">Back-to-back</span><span>Grace</span></div>
          {human.map(c => {
            const score = d.score[c.key as "human-major" | "human-nmg"]!;
            return <div key={c.key} className="tableRow human" title={c.rule}>
              <span>{c.label}</span>
              <span>{d.live.toFixed(2)}</span>
              <span title={scoreText(d.live, d.graceTicks, dc, d.weights)}>{score2(score)}</span>
              <span title={dc ? `${dc.framePerfect} frame-perfect of ${plural(dc.inputs, "input")}` : undefined}>{dc ? dc.framePerfect : "—"}</span>
              <span>{dc ? dc.backToBack : "—"}</span>
              <span>{d.graceTicks ?? "—"}</span>
            </div>;
          })}
        </div>
      </>}
  </div>;
}
