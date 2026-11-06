"use client";

import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactElement } from "react";
import "./route-picker.css";
import "./fulltas.css";
import { FullTasTransitions } from "./fulltas-transitions";
import { FullTasSolvePanel, LEVERS, SegmentGroup, SegmentLegend, goalSpan, liveSeconds, place, resetSegments, runTime, type FullTasGoal, type FullTasIdle, type FullTasOutcome, type FullTasSolveState, type FullTasSplit } from "./fulltas-solve";
import { FullTasClasses, RouteClasses, type FullTasClassRun, type FullTasDifficultyJob, type FullTasQueue } from "./fulltas-classes";
import { TechPanel, type TechState } from "./techs";
import { PlaceLoadstate, TasEditor, type PlacePrefill, type TasLive } from "./tas-editor";
import { artTiles, BOX_KINDS, boxCost, boxTitle, type BoxCategory, type BoxInfo, type Marker, capturedStates, GROWN_COLOR, inState, MARKERS, SPRING_COLOR, StatePicker, TONE_COLORS, type ArtIndex, type StatePick, type TreeStep, type WorldState } from "./ultra-level";
import { request, subscribe } from "./studio-socket";

type Action = { id: string; label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number; enabled: boolean; rawMoveX?: number; rawJumpPressed?: boolean; rawJumpHeld?: boolean; rawJumpReleased?: boolean; rawDashPressed?: boolean; rawDashHeld?: boolean };
type Route = { id: string; name: string; actionCount: number; actions: Action[] };
type RecordedAction = { label: string; durationTicks: number };
type Recording = { id: string; name: string; createdUtc: string; sampleCount: number; actionCount: number; courseTimeSeconds: number; actions: RecordedAction[] };
type Savestate = { id: string; name: string; createdUtc: string; assigned: boolean };
type Caps = { wallJump: boolean; dashes: number; airJumps: number; blockSwap: boolean; orbs: boolean; zip: boolean; omni: boolean };
type Category = { id: string; label: string; caps: Caps };
type Course = { id: number; token: string; bonus: boolean; categories: string[]; from?: string; to?: string; hint?: string | null; optional?: OptionalBlock[] };
type OptionalBlock = { name: string; note: string; on?: boolean; capabilities: string[] };
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; optional?: string[]; evaluations: number; finishes: number; turboChecks: number; turboSaved: number; toValidate?: number; best: number | null; log: string[]; courses: number[]; minutes?: number; repeat?: boolean; pass?: number; queue?: { label: string; result?: string | null; active: boolean }[];
  auto?: boolean; farm?: boolean; humanClass?: HumanCourseClass | null; focus?: [number, number] | null; waypoints?: Waypoints | null; bestFarm?: number | null;
  outlook?: Outlook | null; pulls?: PullNow[] };
type Outlook = { verdict: "likely" | "possible" | "unlikely" | "exhausted" | "floor"; headline: string; advice: string[]; unit: string;
  best: number; bestTicks: number; floor: number; floorTicks: number; headroom: number; hot: { goal: string; label: string; expected: number; slow: string }[];
  searched: number; sessions: number; pulls: number; gains: number; gained: number; lastGainAgo: number | null; typicalGain: number | null;
  chanceHour: number; medianWait: number; remaining: number; chanceRemaining: number; untried: { lever: string; goals: string[] }[];
  basis: { runs: number; search: number; gains: number; spell: number; spellGains: number; spellSearch: number } };
type PullNow = { pull: number; lever: string; goal: string; label: string; budget: number; expected: number; pulls: number; cpu: number; gained: number;
  misses: number; history: { pulls: number; gains: number } };
type Waypoints = { timing: "record" | "farm"; route: string; goals: string[]; rects: (number[] | null)[]; world: (number[] | null)[]; splits: number[]; timerGoal?: number; record?: number; farm?: number;
  bus?: { gate: BusEdge | null; finish: BusEdge | null } | null;
  loop?: { steady: boolean; laps: number; ticks: number; held: number; live: number; lap: number; settled: number; cycles: number[] } };
type Direction = "forwards" | "backwards";
type HumanCourseClass = "human-major" | "human-nmg";
const HUMAN_LABEL: Record<HumanCourseClass, string> = { "human-major": "Human major", "human-nmg": "Human NMG" };
type CourseTarget = { course: number; capabilities: string; farm: boolean; direction?: Direction; class?: HumanCourseClass };
type FullTasRun = { name: string; start?: string | null; savestate?: string | null; section: "complete" | "wip" | "test"; goals: FullTasGoal[]; breaker?: string | null; best: { routeId: string; name: string; time: number; splits: FullTasSplit[] } | null;
  offline?: { reached: number; time: number; splits: FullTasSplit[]; segments?: number[][]; idles?: FullTasIdle[] | null } | null; closest?: { reached: number; next: string | null; distance: number | null; ticks: number } | null; playable?: boolean; traceReady: boolean }
  & Partial<Omit<FullTasClassRun, "name">>;
type FullTasState = { runs: FullTasRun[]; solve: FullTasSolveState; lastResult?: FullTasOutcome | null; goalsOverlay?: string; queue?: FullTasQueue | null; difficulty?: FullTasDifficultyJob | null };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; humanPlayback?: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean; overlayShown: boolean; breakerLightsForced?: boolean; checkpointRespawns?: boolean; fastPlayback?: boolean; idle1x?: boolean; tasSkin?: boolean; tasSkinOutline?: boolean;
  saveUnlocks?: Caps | null;
  selectedRouteId: string; routes: Route[]; categories: Category[]; courses?: Course[]; recordings: Recording[]; savestates: Savestate[]; solver: SolverState;
  fullTas?: FullTasState;
  techs?: TechState;
  tas?: TasLive | null;
};
type StaticState = { staticVersion: number; routes: Route[]; categories: Category[]; courses?: Course[]; recordings: Recording[]; savestates: Savestate[];
  fullTas?: Pick<FullTasState, "runs">; techs?: Pick<TechState, "list" | "detectors" | "labs"> };
type LiveState = Omit<StudioState, "routes" | "categories" | "courses" | "recordings" | "savestates" | "fullTas" | "techs"> & { staticVersion: number;
  fullTas?: Omit<FullTasState, "runs">; techs?: Pick<TechState, "injectedLab" | "seen"> };

const emptyState: StudioState = { connected: false, version: "", engineStatus: "Connecting…", running: false, recording: false, paused: false, capabilitiesLocked: false, overlayShown: true, selectedRouteId: "", routes: [], categories: [], recordings: [], savestates: [], solver: { running: false, status: "Idle", course: 0, capabilities: "", evaluations: 0, finishes: 0, turboChecks: 0, turboSaved: 0, best: null, log: [], courses: [1, 2, 3] } };

function mergeState(s: StaticState, l: LiveState): StudioState {
  return { ...l, routes: s.routes, categories: s.categories, courses: s.courses, recordings: s.recordings, savestates: s.savestates,
    fullTas: s.fullTas && l.fullTas ? { ...s.fullTas, ...l.fullTas } : undefined,
    techs: s.techs && l.techs ? { ...s.techs, ...l.techs } : undefined };
}

export default function Studio() {
  const [state, setState] = useState<StudioState>(emptyState);
  const [tab, setTab] = useState("fulltas");
  const [error, setError] = useState("");
  const [inert, setInert] = useState<{ routeId: string; text: string | null } | null>(null);
  const [mapTarget, setMapTarget] = useState<MapTarget | null>(null);
  const [courseTargets, setCourseTargets] = useState<CourseTarget[]>([]);
  const setCourseTarget = (t: CourseTarget) => setCourseTargets([t]);
  const [placePrefill, setPlacePrefill] = useState<PlacePrefill | null>(null);
  const saveUnlocks = state.connected ? state.saveUnlocks ?? null : null;

  useEffect(() => {
    let staticPart: StaticState | null = null, livePart: LiveState | null = null;
    return subscribe({
      static(data) { staticPart = data as StaticState; if (livePart) setState(mergeState(staticPart, livePart)); },
      live(data) { livePart = data as LiveState; if (staticPart) setState(mergeState(staticPart, livePart)); },
      status(connected) {
        if (connected) { setError(""); return; }
        // A restarted game counts its versions from the start again.
        staticPart = livePart = null;
        setState(s => ({ ...s, connected: false, engineStatus: "Game plugin offline" }));
      },
    });
  }, []);
  const route = useMemo(() => state.routes.find(r => r.id === state.selectedRouteId) ?? state.routes[0], [state]);
  // A command's reply comes after the state it left (WebStudioServer.Pump), so that state shows when it resolves.
  const command = async (path: string, body: unknown = {}) => {
    try { setError(""); await request(path, body); return true; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return false; }
  };
  const findInert = async () => {
    if (!route) return;
    setError("");
    setInert({ routeId: route.id, text: null });
    try {
      const r = await request("/enginesim/effects", { routeId: route.id });
      const ticks = (ranges: number[][]) => ranges.reduce((n, [a, b]) => n + b - a + 1, 0);
      setInert({ routeId: route.id, text: `Overlay hides ${r.jump.length}/${r.totals.jump} jump presses, ${r.dash.length}/${r.totals.dash} dashes (${r.turn.length} turns), `
        + `${r.release.length}/${r.totals.release} releases, x on ${ticks(r.x)} and y on ${ticks(r.y)} input ticks` });
    } catch (e) {
      setInert(null);
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  return <main className="shell">
    <section className="workspace">
      <div className="toggleBar">
        <div className={`connection ${state.connected ? "online" : "offline"}`}><i />{state.connected ? `Connected · v${state.version}` : "Waiting for game"}</div>
        <label className={`capabilityLock ${state.capabilitiesLocked ? "on" : ""}`} title={state.capabilitiesLocked ? "On: your unlocks never change. Course solving needs this off." : "Off: routes and solving change unlocks to match; the game saves."}>
          <input type="checkbox" checked={state.capabilitiesLocked} disabled={!state.connected} onChange={e => command("/settings/lock-capabilities", { locked: e.target.checked })} />
          <strong>Don&apos;t touch my unlocks</strong>
        </label>
        <label className={`capabilityLock executable ${state.overlayShown ? "on" : ""}`} title="In game: shows spikes, velocity, recent inputs and jumps left.">
          <input type="checkbox" checked={state.overlayShown} disabled={!state.connected} onChange={e => command("/settings/overlay", { shown: e.target.checked })} />
          <strong>Overlay</strong>
        </label>
        <label className={`capabilityLock executable ${state.tasSkin ? "on" : ""}`} title="In game: draws the player as its exact hurtbox instead of its sprite.">
          <input type="checkbox" checked={!!state.tasSkin} disabled={!state.connected} onChange={e => command("/settings/tas-skin", { on: e.target.checked })} />
          <strong>TAS skin</strong>
        </label>
        <label className={`capabilityLock executable ${state.tasSkinOutline ? "on" : ""}`} title="In game: the TAS skin as the hurtbox's outline only, over the game's own sprite.">
          <input type="checkbox" checked={!!state.tasSkinOutline} disabled={!state.connected || !state.tasSkin} onChange={e => command("/settings/tas-skin-outline", { on: e.target.checked })} />
          <strong>Outline only</strong>
        </label>
        <label className={`capabilityLock executable ${state.breakerLightsForced ? "on" : ""}`} title="In game: Area 1 lit as before the breaker. Saved lights unchanged.">
          <input type="checkbox" checked={!!state.breakerLightsForced} disabled={!state.connected} onChange={e => command("/settings/breaker-lights", { on: e.target.checked })} />
          <strong>Breaker lights</strong>
        </label>
        <label className={`capabilityLock executable ${state.checkpointRespawns ? "on" : ""}`} title="On: deaths respawn at checkpoints. Off: at course start. Switch while idle.">
          <input type="checkbox" checked={!!state.checkpointRespawns} disabled={!state.connected || state.running || state.recording || state.solver?.running || state.fullTas?.solve?.running} onChange={e => command("/settings/checkpoint-respawns", { on: e.target.checked })} />
          <strong>Checkpoint respawns</strong>
        </label>
      </div>
      <header className={`topbar ${state.humanPlayback ? "humanized" : ""}`}>
        <div><p className="eyebrow">SELECTED ROUTE</p><h1>{route?.name ?? "No route"}</h1><p className="statusLine"><span className={state.running ? "pulse" : "dot"} />{state.humanPlayback && <span className="humanBadge" title="Human offline best: same time, fewer perfect inputs. Never saved.">HUMANIZED</span>}{state.engineStatus}</p>{inert?.text && inert.routeId === route?.id && <p className="statusLine">{inert.text}</p>}</div>
        <div className="transport">
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/run")}>▶ Run route</button>
          <label className={`fastPlayback ${state.fastPlayback ? "on" : ""}`} title="On: 1x playback doesn't wait for real time and runs as fast as the game renders. Game time per frame stays fixed, so it plays the same. Walks and probes at another frame rate stay paced.">
            <input type="checkbox" checked={!!state.fastPlayback} disabled={!state.connected} onChange={e => command("/settings/fast-playback", { on: e.target.checked })} />Fast
          </label>
          <label className={`fastPlayback ${state.idle1x ? "on" : ""}`} title="On: after a minute with no command or input, each Full-TAS run's current best plays at 1x (Fast) and is logged per tick to the config's 1x folder; a complete run's first log is its 1x reference for the engine's check. Any command or input stops it.">
            <input type="checkbox" checked={!!state.idle1x} disabled={!state.connected} onChange={e => command("/probe/1x/idle", { on: e.target.checked })} />1x when idle
          </label>
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/walk", { slowdown: 10 })} title={"Replays the route 10× slower, human version when it's as fast."}>⏵ Walk route</button>
          <button className="secondary" disabled={!route || inert?.text === null} onClick={findInert} title={"Replays the route offline without each input in turn; the overlay then leaves out the inputs that change nothing in the game. Run it before recording."}>{inert?.text === null ? "Finding inert inputs…" : "Hide inert inputs"}</button>
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/duplicate")}>Duplicate</button>
          <button className="secondary" disabled={!route} onClick={() => { const name=window.prompt("Rename route",route?.name); if(name?.trim()) command("/routes/rename",{routeId:route?.id,name:name.trim()}); }}>Rename</button>
          <button className="danger" disabled={!state.running && !state.recording} onClick={() => command("/routes/stop")}>Stop</button>
        </div>
      </header>

      <nav className="tabs">{[["tas","TAS"],["fulltas","Full-TAS"],["tech","Tech"],["optimize","Optimize"],["recordings","Recordings"],["savestates","Savestates"]].map(([id,label]) => <button key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)}>{label}</button>)}</nav>
      {error && <div className="errorBanner">{error}<button onClick={() => setError("")}>×</button></div>}
      {state.paused && <div className="pauseBanner"><div><strong>The game is paused</strong><span>Automation needs the physics simulation running.</span></div><button onClick={() => command("/game/resume")}>Resume simulation</button></div>}

      <div className="content">

        {tab === "recordings" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">RAW FIXED-FRAME CAPTURES</p><h2>Recordings</h2></div><span className="buttonRow"><span className="hint">{state.recording ? "Recording now…" : `${state.recordings.length} saved`}</span><button className="record" disabled={state.running} onClick={() => command(state.recording ? "/record/stop" : "/record/start")}>{state.recording ? "■ Stop recording" : "● Record"}</button></span></div>{state.recording&&<p className="recordingActive">● Recording is active. Finishing the course saves it automatically.</p>}<div className="recordingList">{state.recordings.map(recording=><div className="recordingCard" key={recording.id}><div className="recordingTitle"><div><strong>{recording.name}</strong><small>{new Date(recording.createdUtc).toLocaleString()} · {recording.sampleCount} physics frames · {recording.actionCount} actions</small></div>{recording.courseTimeSeconds>0&&<b>{recording.courseTimeSeconds.toFixed(3)}s</b>}</div><p>{recording.actions.map(action=>`${action.label.replace(/^Exact /,"")} ${action.durationTicks}f`).join(" → ")}</p><div className="buttonRow"><button className="primary" onClick={()=>command("/recordings/create-route",{recordingId:recording.id})}>Create route</button><button title="Name it Course1_To_Course2 or Course2_To_Bonus2: a transition route." onClick={()=>command("/recordings/create-transition",{recordingId:recording.id})}>Create transition</button><button onClick={()=>{const name=window.prompt("Rename recording",recording.name);if(name?.trim())command("/recordings/rename",{recordingId:recording.id,name:name.trim()});}}>Rename</button><button className="danger" onClick={()=>{if(window.confirm(`Delete recording “${recording.name}”?`)) command("/recordings/delete",{recordingId:recording.id});}}>Delete</button></div></div>)}</div>{!state.recordings.length&&!state.recording&&<div className="emptyState"><span className="orbit">●</span><h2>No recordings yet</h2><p>Press Record, play the route, and finish the course.</p></div>}</div>}

        {tab === "fulltas" && <><FullTas fullTas={state.fullTas} running={state.running} onCommand={command} onMap={name => setMapTarget({ course: 0, capabilities: "", optional: [], fulltas: name })}/>{mapTarget?.fulltas && <SolveMap key={`fulltas_${mapTarget.fulltas}`} target={mapTarget} courses={state.courses ?? []} runs={(state.fullTas?.runs ?? []).map(r => r.name)} goals={state.fullTas?.runs.find(r => r.name === mapTarget.fulltas)?.goals} busy={state.running || !!state.fullTas?.solve.running} onClose={() => setMapTarget(null)}/>}</>}

        {tab === "tas" && <TasEditor tas={state.tas} routes={state.routes} courses={state.courses ?? []} categories={state.categories} savestates={state.savestates} onCommand={command}
          onImprove={(course, capabilities) => { setCourseTarget({ course, capabilities, farm: false }); setTab("optimize"); }} onPlace={p => { setPlacePrefill(p); setTab("savestates"); }}/>}

        {tab === "tech" && <TechPanel techs={state.techs} running={state.running || state.recording} onCommand={command}/>}


        {tab === "optimize" && <><RoutePicker routes={state.routes} courses={state.courses ?? []} categories={state.categories} activeId={route?.id} saveUnlocks={saveUnlocks} targets={courseTargets} onTargets={setCourseTargets} onCommand={command}/><div className="optimizeGrid"><Solver solver={state.solver ?? emptyState.solver} courses={state.courses ?? []} categories={state.categories} routes={state.routes} running={state.running} targets={courseTargets} onTargets={setCourseTargets} onCommand={command} onMap={setMapTarget}/><RouteClasses route={route ?? null}/></div>{state.fullTas && state.fullTas.runs.length > 0 && <FullTasTransitions runs={state.fullTas.runs} solve={state.fullTas.solve} lastResult={state.fullTas.lastResult} busy={state.running || state.fullTas.solve.running} onMap={(name, stretch) => setMapTarget({ course: 0, capabilities: "", optional: [], fulltas: name, stretch })} onCommand={command}/>}{mapTarget && (mapTarget.fulltas
          ? <SolveMap key={`fulltas_${mapTarget.fulltas}_${mapTarget.stretch ?? "all"}`} target={mapTarget} courses={state.courses ?? []} runs={(state.fullTas?.runs ?? []).map(r => r.name)} goals={state.fullTas?.runs.find(r => r.name === mapTarget.fulltas)?.goals} busy={state.running || !!state.fullTas?.solve.running} onClose={() => setMapTarget(null)}/>
          : <SolveMap key={`${mapTarget.course}_${mapTarget.capabilities}_${mapTarget.optional.join("+")}`} target={mapTarget} courses={state.courses ?? []} onClose={() => setMapTarget(null)}/>)}</>}

        {tab === "savestates" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">START STATES</p><h2>Savestates</h2></div><button className="primary" onClick={() => command("/savestates/capture")}>Capture current state</button></div><p className="lede">Capture a game state, then assign it as a route start.</p><div className="savestateList">{!state.savestates.length && <div className="emptyState"><span className="orbit">●</span><h2>No savestates yet</h2><p>Capture one to start routes mid-game.</p></div>}{state.savestates.map(cp => <div className={`savestate ${cp.assigned ? "assigned" : ""}`} key={cp.id}><div><strong>{cp.name}</strong><small>{new Date(cp.createdUtc).toLocaleString()}</small></div>{cp.assigned && <span className="badge">Route start</span>}<button onClick={() => command("/savestates/restore", { savestateId: cp.id })}>Restore</button><button className="secondary" onClick={() => command("/savestates/assign", { savestateId: cp.assigned ? "" : cp.id })}>{cp.assigned ? "Unassign" : "Use as start"}</button></div>)}</div><PlaceLoadstate savestates={state.savestates} prefill={placePrefill} onCommand={command}/></div>}
      </div>
    </section>
  </main>;
}

const CATEGORY_ROUTE = /^([^_\s]+)_([A-Z][A-Z0-9_]*) (farm |forwards |backwards |human-major |human-nmg )?([\d.]+)$/i;

function capsSubset(a: Caps, b: Caps) {
  return (!a.wallJump || b.wallJump) && a.dashes <= b.dashes && a.airJumps <= b.airJumps
    && (!a.blockSwap || b.blockSwap) && (!a.orbs || b.orbs) && (!a.zip || b.zip) && (!a.omni || b.omni);
}

function shortCaps(caps: Caps) {
  const parts: string[] = [];
  if (caps.wallJump) parts.push("WJ");
  if (caps.dashes > 0) parts.push(`DS${caps.dashes > 1 ? caps.dashes : ""}`);
  if (caps.airJumps > 0) parts.push(`DJ${caps.airJumps > 1 ? caps.airJumps : ""}`);
  if (caps.blockSwap) parts.push("BS");
  if (caps.orbs) parts.push("ORB");
  if (caps.zip) parts.push("ZIP");
  if (caps.omni) parts.push("OM");
  return parts.length ? parts.join(" + ") : "None";
}

function describeCaps(caps: Caps) {
  const parts: string[] = [];
  if (caps.wallJump) parts.push("Wall jump");
  if (caps.dashes > 0) parts.push(`dash (x${caps.dashes})`);
  if (caps.airJumps > 0) parts.push(`double jump (x${caps.airJumps})`);
  if (caps.blockSwap) parts.push("block swap");
  if (caps.orbs) parts.push("refill orbs");
  if (caps.zip) parts.push("zip movers");
  if (caps.omni) parts.push("omnidash");
  return parts.length ? parts.join(", ") : "Nothing";
}

const BONUS_WHERE: Record<string, string> = {
  bonus1: "Below Course 2, right of its restart. Starts right of Course 2.",
  bonus2: "Below and left of Course 3's start. Starts just left of it.",
  bonus3: "Right of Course 3's restart, higher. Starts at that restart.",
  bonus4: "Straight below Course 5's start. Starts at Course 5's restart.",
  bonus5: "Below Course 3's left upgrades (buyfree3). Starts at Course 3's restart.",
  bonus6: "Above Course 3, left of Course 4 (buyfree4c). Starts at Course 4.",
  bonus7: "Left of Course 4's upgrades (buyfree4a), higher. Starts at Course 4.",
  bonus8: "Far above Course 4 (Course 6's box). Starts at Checkpoint (2) (20201, 13045).",
  bonus9: "Below and left of Course 9's start. Starts at the checkpoint at (16669, 3213).",
  bonus10: "Left of Course 7's start, lower. Starts at Checkpoint (4) (13063, 1482).",
  bonus11: "Just below Course 7's start, right. Starts at the checkpoint by the atom room (22644, -375), its door open.",
  bonus12: "Above Course 7, below Course 9. Starts at Course 7.",
  bonus13: "Below Course 10, slightly left. Starts at the checkpoint at (24931, -487).",
  bonus14: "Right of Course 10. Starts at Course 10's restart.",
  bonus15: "Far left of Course 5, high up. Starts at Course 5's restart.",
  bonus16: "Above Course 1 (buyfree4b), level with Bonus 6. Starts at Course 4.",
  bonus17: "Far below Course 7 (jiggle drops). Starts at Checkpoint (1) (24619, -1618).",
  bonus18: "Left of Course 8's start (clone dust). Starts at Course 8.",
  bonus19: "The Tree in Area 1 (plants it). Starts at Course 10.",
  bonus20: "Vman start, overgrown Area 1, above Course 10. Starts at Course 10.",
  bonus21: "The other Vman start, just above Bonus 20. Starts at Course 10.",
  bonus22: "Above Course 5's start (secret #2, buyfree5). Starts at Course 5.",
  bonus23: "Left of Course 4's upgrades (buyblockswap5, after breaker). Starts at Course 5.",
  unlockjumpdash: "Far below Course 7 (jump and dash refresh orbs; The Tree at level 10). Starts at Checkpoint (1) (24619, -1618).",
};
const BONUS_TOKENS: Record<string, string> = { buyfree3: "bonus5", buyfree4a: "bonus7", buyfree4b: "bonus16", buyfree4c: "bonus6", buyfree5: "bonus22", buyblockswap5: "bonus23" };

function placeTitle(c: Course | undefined): string | undefined {
  if (!c) return undefined;
  const where = (p?: string) => p ? BONUS_WHERE[p.toLowerCase()] : undefined;
  if (c.to) { const end = where(c.to), start = where(c.from); return [start && `From ${place(c.from ?? "")}: ${start}`, end && `To ${place(c.to)}: ${end}`].filter(Boolean).join("\n") || undefined; }
  return [where(c.token), c.hint].filter(Boolean).join("\n") || undefined;
}

function goalTitle(label: string): string | undefined {
  const m = /^(reward|buy):(.+)$/.exec(label);
  if (!m) return undefined;
  const parts = m[2].split("+").map(t => t.trim().toLowerCase()).map(t => m[1] === "reward" ? t : BONUS_TOKENS[t]).filter((t): t is string => !!t && !!BONUS_WHERE[t]);
  return parts.map(t => `${place(t)}: ${BONUS_WHERE[t]}`).join("\n") || undefined;
}

function courseLabel(c: Course | undefined, id: number) {
  if (!c) return `Course ${id}`;
  return c.to ? `${place(c.from ?? "")} → ${place(c.to)}` : place(c.token);
}

type Bests = Map<string, {route: Route; time: number}>;

function categoryBests(routes: Route[], courses: Course[], categories: Category[]) {
  const best: Bests = new Map(), farm: Bests = new Map(), forwards: Bests = new Map(), backwards: Bests = new Map();
  const human: Record<HumanCourseClass, Bests> = { "human-major": new Map(), "human-nmg": new Map() };
  const kinds: Record<string, Bests> = { "": best, "farm ": farm, "forwards ": forwards, "backwards ": backwards,
    "human-major ": human["human-major"], "human-nmg ": human["human-nmg"] };
  const other: Route[] = [];
  for (const r of routes) {
    const m = CATEGORY_ROUTE.exec(r.name);
    const course = m && courses.find(c => c.token.toLowerCase() === m[1].toLowerCase());
    const cat = m ? categories.find(c => c.label.toLowerCase() === m[2].toLowerCase()) : undefined;
    if (!m || !course || !cat || !course.categories.includes(cat.id)) { other.push(r); continue; }
    const kind = kinds[(m[3] ?? "").toLowerCase()], key = `${course.id}:${cat.id}`, time = Number(m[4]);
    const current = kind.get(key);
    if (!current || time < current.time) kind.set(key, {route: r, time});
    else other.push(r);
  }
  return {best, farm, forwards, backwards, human, other};
}

function soloBest(best: Bests, c: Course, cat: Category, categories: Category[]) {
  let solo: {route: Route; time: number} | undefined;
  for (const narrower of categories.filter(n => c.categories.includes(n.id) && capsSubset(n.caps, cat.caps))) {
    const b = best.get(`${c.id}:${narrower.id}`);
    if (b && (!solo || b.time < solo.time)) solo = b;
  }
  return solo;
}

const DIRECTION_BLOCK: Record<Direction, string> = { forwards: "avoid-backwards", backwards: "avoid-forwards" };
const twoWay = (c: Course | undefined, cat: string) => Object.values(DIRECTION_BLOCK).every(n => c?.optional?.some(b => b.name === n && b.capabilities.includes(cat)));

function directionBest(bests: ReturnType<typeof categoryBests>, c: Course, cat: Category, categories: Category[], direction?: Direction) {
  if (direction === "backwards") return soloBest(bests.backwards, c, cat, categories);
  return (direction === "forwards" ? soloBest(bests.forwards, c, cat, categories) : undefined) ?? soloBest(bests.best, c, cat, categories);
}

const sameTarget = (a: CourseTarget, b: CourseTarget) => a.course === b.course && a.capabilities === b.capabilities && !!a.farm === !!b.farm && a.direction === b.direction && a.class === b.class;
function targetLabel(t: CourseTarget, courses: Course[], categories: Category[]) {
  const c = courses.find(x => x.id === t.course), lapless = !!c && (c.bonus || !!c.to);
  return `${courseLabel(c, t.course)} · ${categories.find(x => x.id === t.capabilities)?.label ?? t.capabilities} · ${t.class ? HUMAN_LABEL[t.class]
    : t.farm && !lapless ? "Farm" : t.direction === "backwards" ? "Backwards" : t.direction === "forwards" ? "Forwards" : "Record"}`;
}

function RoutePicker({routes,courses,categories,activeId,saveUnlocks,targets,onTargets,onCommand}:{routes:Route[];courses:Course[];categories:Category[];activeId?:string;saveUnlocks:Caps|null;targets:CourseTarget[];onTargets:(t:CourseTarget[])=>void;onCommand:(path:string,body?:unknown)=>Promise<boolean>}) {
  const bests = categoryBests(routes, courses, categories), {farm, other} = bests;
  const [human, setHuman] = useState(false);
  const slot = (label: string, t: CourseTarget, b: {route: Route; time: number} | undefined, digits: number) => {
    const on = targets.some(x => sameTarget(x, t));
    return <button className={`pickSlot ${b ? "" : "empty"} ${b && b.route.id === activeId ? "active" : ""} ${on ? "targeted" : ""}`}
      title={`${b?.route.name ?? "No route yet: Improve a course finds a first finish"}\nClick to improve it; ctrl+click to add it to the targets`}
      onClick={e => {
        const many = e.ctrlKey || e.metaKey;
        onTargets(!many ? [t] : on ? targets.filter(x => !sameTarget(x, t)) : [...targets, t]);
        if (b && !(many && on)) onCommand("/routes/select", { routeId: b.route.id });
      }}><small>{label}</small>{b ? b.time.toFixed(digits) : "—"}</button>;
  };
  const groups: [string, Course[]][] = [
    ["COURSES", courses.filter(c => !c.bonus && !c.to)],
    ["TRANSITIONS", courses.filter(c => c.to)],
    ["BONUSES", courses.filter(c => c.bonus && !c.to)]];
  return <div className="panel routePicker"><div className="panelTitle"><div><p className="eyebrow">ROUTES</p><h2>Select a route</h2></div><span className="hint"><span className="segmented">
      <button className={human ? "secondary" : "primary"} onClick={() => setHuman(false)}>Records</button>
      <button className={human ? "primary" : "secondary"} title="Each course's human-class records, by score: time + spike grace + frame-perfect inputs + back-to-back pairs" onClick={() => setHuman(true)}>Human</button></span> · {targets.length > 1 && <>{targets.length} targets · <button className="secondary" onClick={() => onTargets([])}>Clear</button> · </>}{routes.find(r => r.id === activeId)?.name ?? "No route selected"}</span></div>
    <div className="matrixScroll"><table className="matrix">
      <thead><tr><th />{categories.map(cat => <th key={cat.id} title={describeCaps(cat.caps)}>{cat.label}</th>)}</tr></thead>
      <tbody>
        {groups.map(([label, list]) => list.some(c => c.categories.length > 0) && <Fragment key={label}>
          <tr className="matrixSection"><th colSpan={categories.length + 1}>{label}</th></tr>
          {list.filter(c => c.categories.length > 0).sort((a, b) => a.id - b.id).map(c => {
            const lapless = !!(c.bonus || c.to);
            return <tr key={c.id}><th title={placeTitle(c)}>{courseLabel(c, c.id)}</th>
              {categories.map(cat => {
                if (!c.categories.includes(cat.id)) return <td key={cat.id} className="na" />;
                const beyond = !!saveUnlocks && !capsSubset(cat.caps, saveUnlocks), both = twoWay(c, cat.id);
                const at = { course: c.id, capabilities: cat.id };
                if (human) return lapless ? <td key={cat.id} className="na" /> : <td key={cat.id} className={beyond ? "beyondUnlocks" : ""}><div className="pickSlots">
                  {(Object.keys(HUMAN_LABEL) as HumanCourseClass[]).map(k => <Fragment key={k}>{slot(k === "human-major" ? "Major" : "NMG", { ...at, farm: false, class: k }, soloBest(bests.human[k], c, cat, categories), 2)}</Fragment>)}
                </div></td>;
                return <td key={cat.id} className={beyond ? "beyondUnlocks" : ""} title={beyond ? `Your unlocks only reach ${shortCaps(saveUnlocks!)}` : undefined}><div className="pickSlots">
                  {slot("Fwd", { ...at, farm: false, direction: both ? "forwards" : undefined }, directionBest(bests, c, cat, categories, "forwards"), 2)}
                  {!lapless && <>
                    {both ? slot("Bwd", { ...at, farm: false, direction: "backwards" }, directionBest(bests, c, cat, categories, "backwards"), 2)
                      : <span className="pickSlot empty" title="One way: no backwards"><small>Bwd</small>·</span>}
                    {slot("Farm", { ...at, farm: true }, soloBest(farm, c, cat, categories), 4)}
                  </>}
                </div></td>;
              })}</tr>;
          })}
        </Fragment>)}
      </tbody>
    </table></div>
    {other.length > 0 && <details className="otherRoutes"><summary className="sectionLabel">OTHER ROUTES ({other.length})</summary>
      <div className="routeList">{other.map(item => <div className={`routeRow ${item.id === activeId ? "active" : ""}`} key={item.id} onContextMenu={(event) => { event.preventDefault(); if (window.confirm(`Delete route “${item.name}”?`)) onCommand("/routes/delete", { routeId: item.id }); }}><button className="routeItem" onClick={() => onCommand("/routes/select", { routeId: item.id })}><div><strong>{item.name}</strong><small>{item.actionCount} actions</small></div></button></div>)}</div>
    </details>}
  </div>;
}
function Field({label,children}:{label:string,children:React.ReactNode}) { return <label className="field"><span>{label}</span>{children}</label>; }
function waypointName(label: string, world: number[] | null | undefined) {
  if (label.startsWith("start:")) return "Start gate (the timer starts)";
  if (label.startsWith("farm:")) return "Finish";
  const m = /^at:[-\d.]+,[-\d.]+,[\d.]+,[\d.]+(?:,from=(.+))?$/.exec(label);
  if (!m || !world) return label;
  return `Area at ${Math.round(world[0])}, ${Math.round(world[1])}${m[1] ? ` · entered from ${m[1].replace(/\+/g, "/")}` : ""}`;
}
const waypointSpan = ([a, b]: [number, number]) => a === b ? `waypoint ${a}` : `waypoints ${a}–${b}`;

function Solver(props:{solver:SolverState;courses:Course[];categories:Category[];routes:Route[];running:boolean;targets:CourseTarget[];onTargets:(t:CourseTarget[])=>void;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(target:MapTarget)=>void}) {
  return props.targets.length>1?<SolveMany {...props}/>:<SolveOne {...props}/>;
}

function SolveOne({solver,courses,categories,routes,running,targets,onTargets,onCommand,onMap}:{solver:SolverState;courses:Course[];categories:Category[];routes:Route[];running:boolean;targets:CourseTarget[];onTargets:(t:CourseTarget[])=>void;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(target:MapTarget)=>void}) {
  const target=targets[0]??null;
  const chosen=target?.course??solver.courses[0]??1;
  const chosenCourse=courses.find(c=>c.id===chosen);
  const capsOptions=chosenCourse&&chosenCourse.categories.length?categories.filter(cat=>chosenCourse.categories.includes(cat.id)):categories;
  const chosenCat=capsOptions.find(cat=>cat.id===target?.capabilities)??capsOptions[0];
  const chosenCaps=chosenCat?.id??"";
  const lapless=!!chosenCourse&&(chosenCourse.bonus||!!chosenCourse.to);
  const cls=lapless?undefined:target?.class;
  const farm=!!target?.farm&&!lapless&&!cls;
  const direction=!farm&&!cls&&twoWay(chosenCourse,chosenCaps)?target?.direction:undefined;
  const [optional,setOptional]=useState<string[]>([]);
  const optionalBlocks=(chosenCourse?.optional??[]).filter(b=>b.capabilities.includes(chosenCaps)&&!(direction&&Object.values(DIRECTION_BLOCK).includes(b.name)));
  const chosenOptional=[...optionalBlocks.filter(b=>!!b.on!==optional.includes(b.name)).map(b=>b.name),...direction?[DIRECTION_BLOCK[direction]]:[]].sort();
  const job={course:chosen,capabilities:chosenCaps,optional:chosenOptional,farm,...cls?{class:cls}:{}};
  const bests=categoryBests(routes,courses,categories),{best,farm:farms}=bests;
  const solo=chosenCourse&&chosenCat?directionBest(bests,chosenCourse,chosenCat,categories,direction):undefined;
  const lap=chosenCourse&&chosenCat?soloBest(lapless?best:farms,chosenCourse,chosenCat,categories):undefined;
  const humanBest=cls&&chosenCourse&&chosenCat?soloBest(bests.human[cls],chosenCourse,chosenCat,categories):undefined;
  const improvable=!!solo||!!lap;
  const key=JSON.stringify([job,solo?.route.name,lap?.route.name]);
  const [fetched,setFetched]=useState<{key:string;waypoints?:Waypoints;error?:string}|null>(null);
  useEffect(()=>{
    if(!improvable) return;
    let live=true;
    request("/solver/waypoints",JSON.parse(key)[0]).then((w:Waypoints)=>{ if(live) setFetched({key,waypoints:w}); },e=>{ if(live) setFetched({key,error:e instanceof Error?e.message:String(e)}); });
    return ()=>{ live=false; };
  },[key,improvable]);
  const mine=solver.course===chosen&&solver.capabilities===chosenCaps&&!!solver.farm===farm&&(solver.humanClass??undefined)===cls;
  const waypoints=(mine?solver.waypoints:null)??(fetched?.key===key?fetched.waypoints:undefined);
  const note=!improvable?"No route yet: Solve finds a first finish; Improve then places waypoints on it."
    :waypoints?undefined:fetched?.key===key?fetched.error:"Placing waypoints on the route…";
  const [picked,setPicked]=useState<[number,number]|null>(null);
  useEffect(()=>setPicked(null),[chosen,chosenCaps,farm,direction]);
  const count=waypoints?.goals.length??0;
  const focus=picked&&picked[1]<=count?picked:null;
  const pick=(first:number,last:number,shift:boolean)=>setPicked(p=>shift&&p?[Math.min(p[0],first),Math.max(p[1],last)]:p&&p[0]===first&&p[1]===last?null:[first,last]);
  const tickAt=(goal:number)=>goal<1?0:waypoints?.splits[goal-1]??-1;
  const focusTicks=focus&&tickAt(focus[1])>=0&&tickAt(focus[0]-1)>=0?tickAt(focus[1])-tickAt(focus[0]-1):null;
  const mapWaypoints:MapGoal[]=(waypoints?.goals??[]).flatMap((label,i)=>{ const r=waypoints!.world[i]; return r?[{index:i+1,label,box:[r[0]-r[2]/2,r[1]-r[3]/2,r[0]+r[2]/2,r[1]+r[3]/2]}]:[]; });
  const route=cls?humanBest:farm?lap:solo;
  const runRoute=async()=>{ if(route&&await onCommand("/routes/select",{routeId:route.route.id})) await onCommand("/routes/run"); };
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">OFFLINE SEARCH</p><h2>Improve a course</h2></div><span className={`offlineStatus ${solver.running?"running":""}`}>{solver.running?solver.auto?"Improving":"Solving":"Idle"}</span></div>
    <p className="lede">Captures the start in game, then improves the route offline.</p><details className="howTo"><summary>How it works</summary><ol><li><b>Improve</b> searches from the target&apos;s route and picks where to look itself.</li><li>Waypoints split the course into 1–3 s stretches; momentum carries through them.</li><li>Think a stretch is slow? Click its waypoints (shift-click extends), then Improve.</li><li>Record: the game timer, gate to gate. Farm: the steady loop, finish to finish by a quick restart or the drop through the end, a lap&apos;s live time (ticks and dash freezes).</li><li>Every offline best is replayed in game (turbo check); held-up bests save as record, and a farm loop that repeats exactly as farm.</li><li>No route yet: <b>Solve</b> finds a first finish.</li></ol></details>
    <div className="offlineControls">
      <div className="field"><span>Target (pick a slot in the grid above; ctrl+click picks several)</span>
        <strong title={placeTitle(chosenCourse)}>{courseLabel(chosenCourse,chosen)} · {chosenCat?.label??"—"} · {cls?`${HUMAN_LABEL[cls]}${humanBest?` · score ${humanBest.time.toFixed(2)}`:" · none yet"}`:farm?`Farm${lap?` · ${lap.time.toFixed(4)}s a lap`:""}`:`${direction==="backwards"?"Backwards record":direction==="forwards"?"Forwards record":"Record"}${solo?` · ${solo.time.toFixed(2)}s`:""}`}</strong>
        <button className="secondary" disabled={solver.running} onClick={()=>{ let id=1; while(solver.courses.includes(id)) id++; onCommand("/courses/add-from-recording").then(ok=>{ if(ok) onTargets([{course:id,capabilities:"",farm:false}]); }); }}>+ New course from last recording</button></div>
      {optionalBlocks.length>0&&<div className="field"><span>Optional avoid blocks</span>{optionalBlocks.map(b=><label key={b.name} className="passLoop" title={b.note}><input type="checkbox" disabled={solver.running} checked={chosenOptional.includes(b.name)} onChange={()=>setOptional(o=>o.includes(b.name)?o.filter(n=>n!==b.name):[...o,b.name])}/>{b.name}</label>)}</div>}
    </div>
    <div className="courseWaypoints">
      <p className="eyebrow">WAYPOINTS · {farm?"FARM":"RECORD"}{waypoints?` · ON ${waypoints.route}`:""}</p>
      {note&&<p className="waypointNote">{note}</p>}
      {waypoints?.loop&&<p className="waypointNote">{waypoints.loop.steady
        ?<>Loop: <b>{waypoints.loop.lap.toFixed(4)} s a lap</b>{waypoints.loop.laps>1?`, a loop of ${waypoints.loop.laps} laps`:""} ({waypoints.loop.ticks} ticks, {waypoints.loop.held} held frames a loop), repeating from loop {waypoints.loop.settled+1}</>
        :<>Doesn&apos;t repeat exactly: each loop&apos;s cycle {waypoints.loop.cycles.map(c=>c<0?"—":`${(c/1400).toFixed(4)} s`).join(", ")}</>}</p>}
      {waypoints&&<div className={`ftFocus ${focus?"on":""}`}>
        {focus
          ? <><span>Improve selected: <b>{waypointSpan(focus)}</b>{focusTicks!=null?` (${focusTicks} ticks, ${(focusTicks*TICK_SECONDS).toFixed(2)} s on this route)`:""}</span><button className="secondary" onClick={()=>setPicked(null)}>Clear selection</button></>
          : <span>Improve searches the whole course. Think a stretch is slow? Click its waypoints below (shift-click extends).</span>}
      </div>}
      {waypoints?.bus&&<NearMisses bus={waypoints.bus} farm={waypoints.timing==="farm"}/>}
      {waypoints&&<div className="resultTable">
        <div className="tableHead"><span>#</span><span>Waypoint</span><span>Split</span><span>From restart</span></div>
        {waypoints.goals.map((label,i)=>{ const at=tickAt(i+1), before=tickAt(i), inPick=focus!=null&&i+1>=focus[0]&&i+1<=focus[1];
          return <div key={i} className={`tableRow ${at<0?"pending":""} ${inPick?"picked":""}`} title={`${label}\nClick to improve from here; shift-click extends`} onClick={e=>pick(i+1,i+1,e.shiftKey)}>
            <span>{i+1}</span><span>{waypoints.loop&&i+1<=(waypoints.timerGoal??0)?`Lead lap ${i+1} finish`:waypointName(label,waypoints.world?.[i])}</span><span>{at>=0&&before>=0?tickText(at-before):"—"}</span><span>{at>=0?tickText(at):"—"}</span></div>; })}
      </div>}
    </div>
    <div className="ftImprove">{solver.running
      ? <button className="danger" onClick={()=>onCommand("/solver/stop")}>Stop</button>
      : <button className="primary" disabled={!!cls&&!improvable} title={cls&&!improvable?"No route yet: improve the record first.":cls?`Searches for a lower ${HUMAN_LABEL[cls]} score (digital stick, two jump buttons) from the routes a human class plays to the finish.`:!improvable?"Finds a first finish (the plain solve).":focus?`Searches only stretches starting in ${waypointSpan(focus)}; known faster pieces still join anywhere.`:`Searches the whole course from its ${farm?"farm route":"record"} until its minutes run out or Stop.`}
        onClick={()=>onCommand("/solver/start",focus?{...job,focus}:job)}>{cls?focus?`Improve ${waypointSpan(focus)} · ${HUMAN_LABEL[cls]}`:`Improve ${HUMAN_LABEL[cls]} score`:!improvable?"Solve":focus?`Improve ${waypointSpan(focus)}`:farm?"Improve farm":direction?`Improve ${direction} record`:"Improve record"}</button>}</div>
    <div className="buttonRow"><button className="secondary" onClick={()=>onMap({course:chosen,capabilities:chosenCaps,optional:chosenOptional,waypoints:mapWaypoints,picked:focus})} title="Draws the explored area, saved routes, best, waypoints and forced-path boxes.">Show map</button><button className="secondary" disabled={!route||running||solver.running} onClick={runRoute} title={route?`Run ${route.route.name} in the game`:"No saved route for this course and category"}>▶ Run route</button><button className="secondary" disabled={running||solver.running} onClick={()=>onCommand("/enginesim/play",{course:chosen,capabilities:chosenCaps})} title={"Plays this category's newest offline best. Nothing is saved."}>▶ Play offline best</button><button className="secondary humanButton" disabled={running||solver.running} onClick={()=>onCommand("/enginesim/play",{course:chosen,capabilities:chosenCaps,human:true})} title={"Plays the humanized best for practice. Nothing is saved."}>▶ Play human version</button></div>
    <div className="offlineSummary"><div><span>{solver.farm?"Best farm":"Best"}</span><strong>{(solver.farm?solver.bestFarm:solver.best)!=null?`${(solver.farm?solver.bestFarm:solver.best)!.toFixed(solver.farm?4:2)}s`:"—"}</strong><small>{solver.course?`${courseLabel(courses.find(c=>c.id===solver.course),solver.course)} · ${solver.capabilities}${solver.optional?.length?` +${solver.optional.join(" +")}`:""}${solver.focus?` · ${waypointSpan(solver.focus)}`:""}`:"Not started"}</small></div><div><span>{solver.auto?"Pulls":"Runs"}</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.auto?"short searches Improve chose":`${solver.finishes.toLocaleString()} timed finishes`}</small></div><div><span>Turbo checks</span><strong>{solver.turboChecks.toLocaleString()}</strong><small>{solver.turboSaved.toLocaleString()} saved as routes</small></div><div><span>To validate</span><strong>{(solver.toValidate??0).toLocaleString()}</strong><small>offline bests waiting for a turbo check</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.outlook&&<OutlookCard outlook={solver.outlook} pulls={solver.running?solver.pulls??[]:[]}/>}
    {solver.log.length>0&&<details className="solverLogBox" open={!solver.auto}><summary>Engine log (latest {solver.log.length} lines)</summary><ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul></details>}
  </div>;
}

type BusEdge = { tick: number; timerTick?: number | null; before: number | null; at: number | null; travel: number | null; after?: number | null };

const units = (v: number | null) => v == null ? "—" : `${Math.abs(v).toFixed(2)} u`;

function NearMisses({bus,farm}:{bus:NonNullable<Waypoints["bus"]>;farm:boolean}) {
  const row=(name:string,e:BusEdge,meaning:string)=><div className="tableRow" title={meaning}>
    <span>{name}</span>
    <span>{e.before!=null&&e.before>0?<><b>{units(e.before)}</b> short at tick {e.tick-1}</>:`touching at tick ${e.tick-1}`}</span>
    <span>{e.at!=null&&e.at>0?"touched only at the tick's start (a position write)":`${units(e.at)} in`} at tick {e.tick}{e.timerTick!=null?` (timer from ${e.timerTick})`:""}{e.timerTick!=null&&e.after!=null&&e.after>0?" · out of the gate the tick after: no tick to gain":""}</span>
    <span>{units(e.travel)}</span></div>;
  return <div className="nearMisses">
    <p className="eyebrow">NEAR MISSES · HURTBOX TO TRIGGER, AFTER EACH PHYSICS TICK</p>
    <div className="resultTable">
      <div className="tableHead"><span>Gate</span><span>Tick before</span><span>First touch</span><span>Moved that tick</span></div>
      {!farm&&bus.gate&&row("Start gate",bus.gate,bus.gate.after!=null&&bus.gate.after>0
        ?`The hurtbox has left the gate by tick ${bus.gate.timerTick!+1}, so the timer already starts on the last tick it can: a shallower first touch would start no timer.`
        :`The timer starts the tick after the first touch. First touch ${units(bus.gate.at)} deep: that much less by tick ${bus.gate.tick} and the timer starts a tick later, a tick off the record.`)}
      {bus.finish&&row("Finish",bus.finish,`Short of the finish the tick before: be ${units(bus.finish.before)} further along by tick ${bus.finish.tick-1} to finish a tick sooner.`)}
    </div>
  </div>;
}

const searchSpan=(s:number)=>s<90?`${Math.round(s)} s`:s<5400?`${Math.round(s/60)} min`:s<172800?`${(s/3600).toFixed(1)} h`:"days";
const pct=(p:number)=>`${Math.round(p*100)}%`;

function OutlookCard({outlook:o,pulls}:{outlook:Outlook;pulls:PullNow[]}) {
  const odds=o.verdict!=="floor"&&o.verdict!=="exhausted";
  const tone=o.verdict==="likely"?"good":o.verdict==="possible"?"maybe":"bad";
  const room=o.bestTicks-o.floorTicks;
  return <div className={`outlookCard ${tone}`}>
    <p className="eyebrow">SHOULD IT KEEP GOING?</p>
    <strong className="outlookHeadline">{o.headline}</strong>
    <ul className="outlookAdvice">{o.advice.map((a,i)=><li key={i}>{a}</li>)}</ul>
    <div className="outlookStats">
      <div><span>Chance of a gain</span><strong>{odds?`${pct(o.chanceHour)} per hour`:"—"}</strong>
        <small>{odds&&o.remaining>0?`${pct(o.chanceRemaining)} in this run's last ${searchSpan(o.remaining)}`:odds?"this run's minutes are up":"more search won't help"}</small></div>
      <div><span>Next gain</span><strong>{odds?`~${searchSpan(o.medianWait)}`:"—"}</strong>
        <small>{odds?`median wait${o.typicalGain!=null?` · gains ~${o.typicalGain} ticks each`:""}`:""}</small></div>
      <div><span>Based on</span><strong>{o.basis.runs?`${o.basis.runs} other run${o.basis.runs===1?"":"s"}`:"no other runs"}</strong>
        <small>{o.basis.runs?`${o.basis.gains} gain${o.basis.gains===1?"":"s"} in ${searchSpan(o.basis.search)} of their search; past ${searchSpan(o.basis.spell)} without one: ${o.basis.spellGains} in ${searchSpan(o.basis.spellSearch)}`:"no recorded Improve here yet, so the odds lean on a flat guess"}</small></div>
      <div><span>This target</span><strong>{searchSpan(o.searched)} searched</strong>
        <small>{o.sessions} Improve run{o.sessions===1?"":"s"} · {o.gains?`${o.gains} gain${o.gains===1?"":"s"} (−${o.gained} ticks), last ${searchSpan(o.lastGainAgo??0)} ago`:"no gain yet"}</small></div>
      <div><span>Room to the floor</span><strong>{room} ticks</strong>
        <small>floor {o.floor.toFixed(2)}s (top speed on every {o.unit}; optimistic) · the model's guess: ~{o.headroom} ticks findable</small></div>
    </div>
    {pulls.length>0&&<div className="outlookNow"><span>Searching now</span><ul>{pulls.map(p=><li key={p.pull}>
      <b>{LEVERS[p.lever]??p.lever}</b> at {o.unit} {p.label} for up to {searchSpan(p.budget*60)} · {p.pulls?`${p.misses?`${p.misses} miss${p.misses===1?"":"es"} in a row here`:`−${p.gained} ticks here`} (${searchSpan(p.cpu)} CPU)`:"first time here"} · {p.history.pulls?`such pulls gained ${p.history.gains} of ${p.history.pulls} times`:"no record of such pulls"}</li>)}</ul></div>}
    <details className="howTo"><summary>How Improve decides</summary><ul>
      <li>A <b>pull</b> is one short search: one tactic at one {o.unit}, for a minute or two. Improve runs pulls until its minutes are up.</li>
      <li>Tactics: <b>window search</b> re-finds the inputs between two {o.unit}s; <b>input polish</b> nudges the timing of the inputs the route has; <b>look-ahead</b> and <b>long windows</b> search further on, so a slower arrival that carries more speed can win; <b>graft</b>, <b>combine</b> and <b>revive</b> splice in faster pieces other found routes have.</li>
      <li>Improve picks the next pull by expected ticks per CPU second: where the model sees slow stretches or another route was faster, and where pulls paid before. A pull that gains nothing makes that spot less likely to be picked again.</li>
      <li>The chance comes from the other Improve runs recorded in this folder: how often they gained once they had gone as long without a gain as this target has now. This target&apos;s own gains then raise it, recent ones most. A few seconds of search can&apos;t swing it; a gain or a long dry spell does. &ldquo;Such pulls gained x of y times&rdquo; counts every recorded pull of that tactic after as many misses in a row at its spot.</li>
      <li>The floor is each {o.unit}&apos;s distance over the fastest speed measured with these unlocks: no route on these {o.unit}s beats it, but it&apos;s optimistic, so being above it doesn&apos;t mean a gain exists.</li>
    </ul></details>
  </div>;
}

type MapTarget = { course: number; capabilities: string; optional: string[]; fulltas?: string; stretch?: string; waypoints?: MapGoal[]; picked?: [number, number] | null };
type MapSolid = { k: "poly" | "line" | "circle" | "capsule" | "area"; spike: boolean; p: number[]; r: number; tone?: "blue" | "orange" | null; spring?: number[] | null; when?: string | null; rings?: number[] | null };
type MapRoute = { name: string; kind: "seed" | "best" | "plateau" | "human" | "closest"; finished: boolean; time: number; timerStart: number; points: number[]; cells?: number[][]; state?: number[] };
type GhostRoute = MapRoute & { cells: number[][]; state: number[] };
type GhostData = { cell: number; a: GhostRoute; b: GhostRoute; diverge: { tick: number; x: number; y: number } | null };
const GHOST_A = "#4fc3f7", GHOST_B = "#ffb74d";
// Every map coordinate is in the game world's frame, whatever FloatingOrigin the capture had.
type MapData = { course: number; capabilities: string; label: string; fulltas?: string; bounds: number[]; solids: MapSolid[];
  grown?: MapSolid[]; treeSteps?: TreeStep[];
  markers: { kind: string; name: string; x: number; y: number; area?: number[][]; spawn?: number[]; when?: string | null; box?: BoxInfo }[]; avoid: number[][]; via: number[][]; goals?: MapGoal[]; optional?: { name: string; note: string; on: boolean; avoid: number[][]; via: number[][] }[]; routes: MapRoute[];
  states?: WorldState[];
  zips?: { name: string; zip: boolean; box: number[]; path: number[][]; corridor: number[][]; when?: string | null }[];
  explored: { cell: number; runs: number; prefixes: number; replayed?: number; toReplay?: number; cells: number[][] };
  pending?: boolean; queued?: boolean };
type MapGoal = { index: number; id?: string; label: string; box: number[] };
type FullTasMapAvoid = { box: number[]; from: number; goal: string | null; when: "always" | "heading" | "after" };
function placeFullTasMap(raw: MapData, ids: string[]): MapData {
  const avoid = (raw.avoid as unknown as FullTasMapAvoid[]).flatMap(a => {
    const at = a.goal == null ? -1 : ids.indexOf(a.goal);
    if (a.when !== "always" && at < 0) return [];
    const [after, until] = a.when === "heading" ? [at, at + 1] : a.when === "after" ? [at + 1, 0] : [0, 0];
    return [[...a.box, after, until, a.from]];
  });
  const goals = (raw.goals ?? []).map(g => ({ ...g, index: g.id ? ids.indexOf(g.id) + 1 : g.index }));
  return { ...raw, avoid, goals };
}
type MapLayers = { art: boolean; background: boolean; tree: boolean; shapes: boolean; explored: boolean; boxes: boolean; markers: boolean } & Record<`box:${BoxCategory}`, boolean>;
const SIDES = ["left", "right", "bottom", "top"];
const avoidLive = (b: number[], g: number) => (!b[4] || g >= b[4]) && (!b[5] || g < b[5]);
const avoidText = (b: number[]) => (b[5] ? `goal ${b[4]}` : b[4] ? `goals ${b[4]}+` : "every goal")
  + (b[6] ? ` · enter from ${SIDES.filter((_, i) => !(b[6] & 1 << i)).join("/") || "inside only"}` : "");
const ROUTE_COLORS = ["#4fc3f7", "#ff8a65", "#ba68c8", "#81c784", "#f06292", "#fff176", "#4db6ac", "#9575cd"];

function SolveMap({target,courses,runs,goals:runGoals,busy,onClose}:{target:MapTarget;courses:Course[];runs?:string[];goals?:FullTasGoal[];busy?:boolean;onClose:()=>void}) {
  const goals = runGoals?.map(g => g.label), goalIds = useRef<string[]>([]);
  goalIds.current = runGoals?.map(g => g.id) ?? [];
  const stretchAt = target.stretch != null ? goalIds.current.indexOf(target.stretch) : -1;
  const [data, setData] = useState<MapData | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState("");
  const [layers, setLayers] = useState<MapLayers>({ art: true, background: false, tree: true, shapes: false, explored: true, boxes: true, markers: true,
    "box:secret": true, "box:bonusUnique": true, "box:bonusBoost": true, "box:achievement": true,
    "box:movement": true, "box:unlock": true, "box:tree": false, "box:course": false });
  const [art, setArt] = useState<ArtIndex | null>(null);
  useEffect(() => { fetch("/art/index.json").then(r => r.ok ? r.json() as Promise<ArtIndex> : null).then(setArt, () => setArt(null)); }, []);
  const artImages = useRef(new Map<string, HTMLImageElement>());
  const [artLoads, setArtLoads] = useState(0);
  const [worldState, setWorldState] = useState<StatePick>({});
  const [hotZip, setHotZip] = useState(-1);
  const markers = useMemo(() => (data?.markers ?? []).filter(m => inState(m.when, worldState)), [data, worldState]);
  const [hidden, setHidden] = useState<Set<number>>(new Set());
  const [cursor, setCursor] = useState<{ x: number; y: number; marker?: string; reached?: number; delta?: number } | null>(null);
  const [boxTip, setBoxTip] = useState<Marker | null>(null);
  const panned = useRef(false);
  const arrivals = useMemo(() => new Map(data?.explored.cells.map(([cx, cy, t]) => [`${cx},${cy}`, t] as [string, number]) ?? []), [data]);
  const [ghostA, setGhostA] = useState("");
  const [ghostB, setGhostB] = useState("");
  const [ghost, setGhost] = useState<GhostData | null>(null);
  const [ghostError, setGhostError] = useState("");
  const [ghostLoading, setGhostLoading] = useState(false);
  useEffect(() => {
    if (!data || !ghostA || !ghostB || ghostA === ghostB) { setGhost(null); setGhostError(""); return; }
    let live = true;
    setGhostLoading(true);
    request("/enginesim/ghost", { course: target.course, capabilities: target.capabilities, fulltas: target.fulltas, a: ghostA, b: ghostB })
      .then((g: GhostData) => { if (live) { setGhost(g); setGhostError(""); } })
      .catch(e => { if (live) { setGhost(null); setGhostError(e instanceof Error ? e.message : String(e)); } })
      .finally(() => { if (live) setGhostLoading(false); });
    return () => { live = false; };
  }, [data, ghostA, ghostB, target]);
  const ghostCells = useMemo(() => {
    if (!ghost) return null;
    const both: { cx: number; cy: number; d: number }[] = [], onlyA: number[][] = [], onlyB: number[][] = [];
    const at = new Map<string, number>(), deltaAt = new Map<string, number>();
    for (const [cx, cy, t] of ghost.b.cells) at.set(`${cx},${cy}`, t);
    for (const [cx, cy, t] of ghost.a.cells) {
      const key = `${cx},${cy}`, tb = at.get(key);
      if (tb === undefined) { onlyA.push([cx, cy]); continue; }
      at.delete(key);
      if (t < 0 || tb < 0) continue;
      both.push({ cx, cy, d: tb - t });
      deltaAt.set(key, tb - t);
    }
    for (const key of at.keys()) { const [cx, cy] = key.split(",").map(Number); onlyB.push([cx, cy]); }
    return { both, onlyA, onlyB, deltaAt, max: both.reduce((m, v) => Math.max(m, Math.abs(v.d)), 0) || 1,
      low: both.length ? both.reduce((m, v) => Math.min(m, v.d), both[0].d) : 0, high: both.length ? both.reduce((m, v) => Math.max(m, v.d), both[0].d) : 0 };
  }, [ghost]);
  const latest = useMemo(() => data?.explored.cells.reduce((m, v) => Math.max(m, v[2]), 0) || 1, [data]);
  const canvas = useRef<HTMLCanvasElement>(null);
  const [drawing, setDrawing] = useState(false);
  const [sketch, setSketch] = useState<number[] | null>(null);
  const [sketchRuns, setSketchRuns] = useState<string[]>(target.fulltas ? [target.fulltas] : []);
  const [avoidScope, setAvoidScope] = useState<"every" | "only" | "from">("every");
  const [avoidGoal, setAvoidGoal] = useState("");
  const [avoidOpen, setAvoidOpen] = useState<string[]>([]);
  const [hotAvoid, setHotAvoid] = useState(-1);
  const focus = target.stretch != null ? stretchAt : target.fulltas && avoidGoal.trim() !== "" && Number.isInteger(Number(avoidGoal)) ? Number(avoidGoal) : null;
  const drawBlock = !sketchRuns.length ? "Pick a run for the boxes." : target.stretch == null && avoidScope !== "every" && focus == null ? "Type a goal number, or click a goal's green box while drawing."
    : avoidOpen.length === SIDES.length ? "A box open on every side cuts nothing." : "";
  const [sketchName, setSketchName] = useState("");
  const [sketchOn, setSketchOn] = useState(false);
  const [saving, setSaving] = useState(false);
  const sketchStart = useRef<{ x: number; y: number } | null>(null);
  const [moving, setMoving] = useState(false);
  const [moved, setMoved] = useState<{ goal: MapGoal; box: number[] } | null>(null);
  const grab = useRef<{ goal: MapGoal; box: number[]; x: number; y: number } | null>(null);
  const colors = useMemo(() => data?.routes.map((r, i) => r.kind === "best" ? "#ffd54f" : r.kind === "human" ? "#c77dff" : r.kind === "closest" ? "#ff5252" : r.kind === "plateau" ? "rgba(255,255,255,.55)" : ROUTE_COLORS[i % ROUTE_COLORS.length]) ?? [], [data]);
  const view = useRef({ ox: 0, oy: 0, scale: 1 });
  const drag = useRef<{ x: number; y: number } | null>(null);
  const fitted = useRef(false);

  const loads = useRef(0);
  const poller = useRef<number | undefined>(undefined);
  const fresh = useRef(true);
  const load = useCallback(async (poll = false) => {
    window.clearTimeout(poller.current);
    const id = poll ? loads.current : ++loads.current;
    if (!poll) { setLoading(true); setFailure(""); fresh.current = true; }
    let pending = false;
    try {
      const raw: MapData = target.fulltas ? await request("/fulltas/map", { name: target.fulltas, poll, ...(target.stretch != null ? { stretch: target.stretch } : {}) })
        : await request("/enginesim/map", { ...target, poll });
      const d = target.fulltas && raw.solids ? placeFullTasMap(raw, goalIds.current) : raw;
      if (id !== loads.current) return;
      pending = !!d.pending;
      if (pending) poller.current = window.setTimeout(() => load(true), 1000);
      if (!d.solids) return;
      setData(d); setFailure(""); setWorldState(w => ({ ...capturedStates(d.states), ...w }));
      if (fresh.current) { setHidden(new Set(d.routes.map((r, i) => r.kind === "plateau" ? i : -1).filter(i => i >= 0))); fresh.current = false; }
      if (!fitted.current) { fit(d); fitted.current = true; }
    }
    catch (e) { if (id === loads.current) setFailure(e instanceof Error ? e.message : String(e)); }
    finally { if (id === loads.current && !pending) setLoading(false); }
  }, [target]);
  useEffect(() => { load(); return () => window.clearTimeout(poller.current); }, [load]);
  const progress = data?.queued ? " (queued behind another map)" : data?.pending && data.explored.toReplay ? ` ${data.explored.replayed ?? 0}/${data.explored.toReplay}` : "";

  function fit(d: MapData) {
    const c = canvas.current; if (!c) return;
    const [x0, y0, x1, y1] = d.bounds, scale = Math.min(c.clientWidth / (x1 - x0), c.clientHeight / (y1 - y0));
    view.current = { scale, ox: (x0 + x1) / 2 - c.clientWidth / 2 / scale, oy: (y0 + y1) / 2 + c.clientHeight / 2 / scale };
  }

  const paintBase = useCallback((g: CanvasRenderingContext2D, w: number, h: number, dpr: number) => {
    if (!data) return;
    const { ox, oy, scale } = view.current;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.fillStyle = "#07111f"; g.fillRect(0, 0, w, h);
    g.setTransform(dpr * scale, 0, 0, -dpr * scale, -ox * scale * dpr, oy * scale * dpr);
    const px = 1 / scale;
    const artShown = !!art && layers.art;
    const drawArt = (pick: StatePick) => {
      for (const t of artTiles(art, "/art/", pick, ox, oy - h / scale, ox + w / scale, oy, px)) {
        let img = artImages.current.get(t.href);
        if (!img) { img = new Image(); img.onload = () => setArtLoads(n => n + 1); img.src = t.href; artImages.current.set(t.href, img); }
        if (!img.complete || !img.naturalWidth) continue;
        g.save(); g.translate(t.x, t.y + t.size); g.scale(1, -1); g.drawImage(img, 0, 0, t.size, t.size); g.restore();
      }
    };
    if (artShown && layers.background) drawArt({ ...worldState, bg: "on" });
    if (artShown) drawArt(worldState);
    if (artShown && layers.tree) drawArt({ ...worldState, tree: "grown" });
    if (artShown && !layers.shapes) for (const s of data.solids) {
      if (!s.spike || !inState(s.when, worldState) || s.k === "circle") continue;
      g.beginPath();
      let at = 0;
      for (const n of s.k === "area" ? s.rings ?? [s.p.length] : [s.p.length]) {
        g.moveTo(s.p[at], s.p[at + 1]);
        for (let i = at + 2; i < at + n; i += 2) g.lineTo(s.p[i], s.p[i + 1]);
        if (s.k === "poly" || s.k === "area") g.closePath();
        at += n;
      }
      g.strokeStyle = "#e5484d"; g.lineWidth = 1.5 * px; g.stroke();
      if (s.k !== "line") { g.fillStyle = "#e5484d"; g.globalAlpha = .3; g.fill("evenodd"); g.globalAlpha = 1; }
    }
    if (layers.explored && data.explored.cells.length) {
      const size = data.explored.cell;
      if (artShown) g.globalAlpha = .55;
      for (const [cx, cy, t] of data.explored.cells) {
        g.fillStyle = t < 0 ? "#34383f" : `hsl(${230 - 230 * t / latest},90%,55%)`;
        g.fillRect(cx * size, cy * size, size, size);
      }
      g.globalAlpha = 1;
    }
    if (!artShown || layers.shapes) for (const s of data.solids) {
      if (!inState(s.when, worldState)) continue;
      const color = s.spring ? SPRING_COLOR : s.tone ? TONE_COLORS[s.tone] : s.spike ? "#e5484d" : "#8193ab";
      g.fillStyle = color; g.strokeStyle = color; g.lineWidth = Math.max(px * 1.5, s.k === "capsule" ? s.r * 2 : 0); g.lineCap = "round";
      g.beginPath();
      if (s.k === "circle") { g.arc(s.p[0], s.p[1], s.r, 0, Math.PI * 2); g.fill(); continue; }
      if (s.k === "area") {
        let at = 0;
        for (const n of s.rings ?? [s.p.length]) {
          g.moveTo(s.p[at], s.p[at + 1]);
          for (let i = at + 2; i < at + n; i += 2) g.lineTo(s.p[i], s.p[i + 1]);
          g.closePath(); at += n;
        }
        g.globalAlpha = s.spike ? .9 : .75; g.fill("evenodd"); g.globalAlpha = 1; continue;
      }
      g.moveTo(s.p[0], s.p[1]);
      for (let i = 2; i < s.p.length; i += 2) g.lineTo(s.p[i], s.p[i + 1]);
      if (s.k === "poly") { g.closePath(); g.globalAlpha = s.spike ? .9 : s.tone ? .5 : .75; g.fill(); g.globalAlpha = 1; } else g.stroke();
      if (s.spring) {
        const [ux, uy] = s.spring, n = s.p.length / 2;
        let cx = 0, cy = 0, reach = 0;
        for (let i = 0; i < n; i++) { cx += s.p[2 * i] / n; cy += s.p[2 * i + 1] / n; }
        for (let i = 0; i < n; i++) reach = Math.max(reach, (s.p[2 * i] - cx) * ux + (s.p[2 * i + 1] - cy) * uy);
        if (px > 6) continue;
        const len = reach + 40, head = 16, tx = cx + ux * len, ty = cy + uy * len;
        g.lineWidth = 2 * px; g.beginPath(); g.moveTo(cx, cy); g.lineTo(tx, ty);
        g.moveTo(tx - head * (ux + uy), ty - head * (uy - ux)); g.lineTo(tx, ty); g.lineTo(tx - head * (ux - uy), ty - head * (uy + ux)); g.stroke();
      }
    }
    for (const s of data.grown ?? []) {
      g.beginPath(); g.moveTo(s.p[0], s.p[1]);
      for (let i = 2; i < s.p.length; i += 2) g.lineTo(s.p[i], s.p[i + 1]);
      g.closePath(); g.fillStyle = "#c5a26b"; g.globalAlpha = .25; g.fill(); g.globalAlpha = 1;
      g.strokeStyle = "#c5a26b"; g.lineWidth = 1.5 * px; g.setLineDash([6 * px, 4 * px]); g.stroke(); g.setLineDash([]);
    }
    for (const t of data.treeSteps ?? []) {
      g.save(); g.setTransform(1, 0, 0, 1, 0, 0);
      const dpr = window.devicePixelRatio || 1, sx = (t.x - ox) * scale * dpr, sy = (oy - t.y) * scale * dpr;
      g.font = `700 ${13 * dpr}px sans-serif`; g.textAlign = "center"; g.textBaseline = "middle";
      g.lineWidth = 3 * dpr; g.strokeStyle = "#07111f"; g.strokeText(String(t.step || "?"), sx, sy); g.fillStyle = GROWN_COLOR; g.fillText(String(t.step || "?"), sx, sy);
      g.restore();
    }
    if (layers.boxes) for (const [x0, y0, x1, y1] of data.via) {
      g.fillStyle = "#3ddc97"; g.globalAlpha = .16; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1;
      g.strokeStyle = "#3ddc97"; g.lineWidth = 2 * px; g.strokeRect(x0, y0, x1 - x0, y1 - y0);
    }
    if (layers.boxes) for (const b of data.avoid) {
      const [x0, y0, x1, y1] = b, from = b[6] ?? 0, a = focus != null && !avoidLive(b, focus) ? .25 : 1;
      g.fillStyle = "#ff3030"; g.globalAlpha = .16 * a; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = a; g.lineWidth = 2 * px;
      [[x0, y0, x0, y1], [x1, y0, x1, y1], [x0, y0, x1, y0], [x0, y1, x1, y1]].forEach(([ax, ay, bx, by], i) => {
        const open = from !== 0 && !(from & 1 << i);
        g.strokeStyle = open ? "#3ddc97" : "#ff3030"; g.setLineDash(open ? [6 * px, 4 * px] : []);
        g.beginPath(); g.moveTo(ax, ay); g.lineTo(bx, by); g.stroke();
      });
      g.setLineDash([]); g.globalAlpha = 1;
    }
    const picked = focus == null ? undefined : data.goals?.find(goal => goal.index - 1 === focus);
    if (picked) { const [x0, y0, x1, y1] = picked.box; g.strokeStyle = "#ffd54f"; g.lineWidth = 3 * px; g.strokeRect(x0, y0, x1 - x0, y1 - y0); }
    if (layers.boxes) for (const block of data.optional ?? [])
      for (const [boxes, color] of [[block.avoid, "#ff3030"], [block.via, "#3ddc97"]] as [number[][], string][])
        for (const [x0, y0, x1, y1] of boxes) {
          if (block.on) { g.fillStyle = color; g.globalAlpha = .16; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1; }
          g.strokeStyle = color; g.lineWidth = 2 * px; g.setLineDash(block.on ? [] : [6 * px, 4 * px]); g.strokeRect(x0, y0, x1 - x0, y1 - y0); g.setLineDash([]);
        }
    if (layers.boxes) for (const wp of target.waypoints ?? []) {
      const [x0, y0, x1, y1] = wp.box, on = !!target.picked && wp.index >= target.picked[0] && wp.index <= target.picked[1], color = on ? "#ffd54f" : "#3ddc97";
      g.fillStyle = color; g.globalAlpha = on ? .28 : .12; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1;
      g.strokeStyle = color; g.lineWidth = (on ? 3 : 2) * px; g.strokeRect(x0, y0, x1 - x0, y1 - y0);
      g.save(); g.setTransform(1, 0, 0, 1, 0, 0);
      const sx = ((x0 - ox) * scale + 4) * dpr, sy = ((oy - y1) * scale + 4) * dpr;
      g.font = `700 ${13 * dpr}px sans-serif`; g.textAlign = "left"; g.textBaseline = "top";
      g.lineWidth = 3 * dpr; g.strokeStyle = "#07111f"; g.strokeText(String(wp.index), sx, sy); g.fillStyle = color; g.fillText(String(wp.index), sx, sy);
      g.restore();
    }
    if (ghost && ghostCells) g.globalAlpha = .15;
    data.routes.forEach((r, i) => {
      if (hidden.has(i) || r.points.length < 4) return;
      g.strokeStyle = colors[i]; g.lineWidth = (r.kind === "best" ? 3 : r.kind === "plateau" ? 1 : 2) * px; g.lineJoin = "round";
      g.beginPath(); g.moveTo(r.points[0], r.points[1]);
      for (let k = 2; k < r.points.length; k += 2) g.lineTo(r.points[k], r.points[k + 1]);
      g.stroke();
      const end = r.points.length - 2;
      g.fillStyle = g.strokeStyle; g.beginPath(); g.arc(r.points[end], r.points[end + 1], 4 * px, 0, Math.PI * 2); g.fill();
      if (r.timerStart >= 0) { g.beginPath(); g.arc(r.points[r.timerStart * 2], r.points[r.timerStart * 2 + 1], 5 * px, 0, Math.PI * 2); g.lineWidth = 1.5 * px; g.strokeStyle = "#fff"; g.stroke(); }
    });
    g.globalAlpha = 1;
    if (ghost && ghostCells) {
      const size = ghost.cell;
      for (const c of ghostCells.both) {
        const m = Math.min(1, Math.abs(c.d) / ghostCells.max);
        g.fillStyle = c.d < 0 ? `rgba(72,185,255,${.18 + .5 * m})` : `rgba(255,91,110,${.18 + .5 * m})`;
        g.fillRect(c.cx * size, c.cy * size, size, size);
      }
      g.lineWidth = px;
      for (const [cells, color] of [[ghostCells.onlyA, GHOST_A], [ghostCells.onlyB, GHOST_B]] as [number[][], string][]) {
        g.strokeStyle = color; g.globalAlpha = .55;
        for (const [cx, cy] of cells) g.strokeRect(cx * size, cy * size, size, size);
      }
      g.globalAlpha = 1;
      for (const [route, color, dash] of [[ghost.a, GHOST_A, []], [ghost.b, GHOST_B, [7 * px, 4 * px]]] as [GhostRoute, string, number[]][]) {
        if (route.points.length < 4) continue;
        g.strokeStyle = color; g.lineWidth = 3 * px; g.lineJoin = "round"; g.setLineDash(dash);
        g.beginPath(); g.moveTo(route.points[0], route.points[1]);
        for (let k = 2; k < route.points.length; k += 2) g.lineTo(route.points[k], route.points[k + 1]);
        g.stroke(); g.setLineDash([]);
        const end = route.points.length - 2;
        g.fillStyle = color; g.beginPath(); g.arc(route.points[end], route.points[end + 1], 5 * px, 0, Math.PI * 2); g.fill();
      }
      if (ghost.diverge) {
        const { x, y, tick } = ghost.diverge;
        g.strokeStyle = "#c77dff"; g.lineWidth = 2.5 * px;
        g.beginPath(); g.arc(x, y, 9 * px, 0, Math.PI * 2); g.stroke();
        g.fillStyle = "#c77dff"; g.beginPath(); g.arc(x, y, 3 * px, 0, Math.PI * 2); g.fill();
        g.save(); g.setTransform(1, 0, 0, 1, 0, 0);
        const screen = window.devicePixelRatio || 1, sx = (x - ox) * scale * screen, sy = (oy - y) * scale * screen;
        g.font = `700 ${12 * screen}px sans-serif`; g.textAlign = "center"; g.textBaseline = "middle";
        g.lineWidth = 3 * screen; g.strokeStyle = "#07111f"; g.strokeText(`states diverge · tick ${tick}`, sx, sy - 17 * screen);
        g.fillStyle = "#c77dff"; g.fillText(`states diverge · tick ${tick}`, sx, sy - 17 * screen);
        g.restore();
      }
    }
    g.strokeStyle = "#ffb74d"; g.lineWidth = 1.5 * px;
    for (const z of data.zips ?? []) if (inState(z.when, worldState)) g.strokeRect(z.box[0], z.box[1], z.box[2] - z.box[0], z.box[3] - z.box[1]);
    if (layers.markers) for (const m of markers) {
      const look = m.kind === "goal" ? { color: "#b9f6ca", shape: "square" } : m.kind === "box" ? BOX_KINDS[m.box!.category] : MARKERS[m.kind];
      if (m.kind === "box" && !layers[`box:${m.box!.category}`]) continue;
      const color = look?.color ?? "#4fc3f7";
      const outline = (polys: number[][], dashed: boolean, fill: number) => {
        g.lineWidth = (dashed ? 1.5 : 2) * px; g.strokeStyle = color; g.fillStyle = color; g.setLineDash(dashed ? [5 * px, 3 * px] : []);
        for (const poly of polys) {
          if (poly.length < 6) continue;
          g.beginPath(); g.moveTo(poly[0], poly[1]);
          for (let i = 2; i + 1 < poly.length; i += 2) g.lineTo(poly[i], poly[i + 1]);
          g.closePath(); g.globalAlpha = fill; g.fill(); g.globalAlpha = 1; g.stroke();
        }
        g.setLineDash([]);
      };
      const icon = art?.icons?.[m.kind];
      if (icon) {
        const href = `/art/${icon.file}.webp`;
        let img = artImages.current.get(href);
        if (!img) { img = new Image(); img.onload = () => setArtLoads(n => n + 1); img.src = href; artImages.current.set(href, img); }
        if (img.complete && img.naturalWidth) { g.save(); g.translate(m.x + icon.dx - icon.w / 2, m.y + icon.dy + icon.h / 2); g.scale(1, -1); g.drawImage(img, 0, 0, icon.w, icon.h); g.restore(); continue; }
      }
      if ((m.kind === "box" || m.kind === "start" || m.kind === "end" || m.kind === "exit" || m.kind === "falseEnding" || m.kind === "trueEnding") && m.area?.length) { outline(m.area, false, m.kind === "box" ? .12 : .15); continue; }
      if (m.kind === "checkpoint") {
        if (m.area) outline(m.area, true, .1);
        if (m.spawn) { g.lineWidth = 2 * px; g.strokeStyle = color; g.strokeRect(m.spawn[0], m.spawn[1], m.spawn[2] - m.spawn[0], m.spawn[3] - m.spawn[1]); continue; }
      }
      g.fillStyle = color;
      const s = (look?.shape === "small" ? 4 : look?.shape === "star" ? 10 : 6) * px;
      if (look?.shape === "circle") { g.beginPath(); g.arc(m.x, m.y, s, 0, Math.PI * 2); g.fill(); }
      else if (look?.shape === "diamond") { g.beginPath(); g.moveTo(m.x, m.y + s * 1.3); g.lineTo(m.x + s, m.y); g.lineTo(m.x, m.y - s * 1.3); g.lineTo(m.x - s, m.y); g.closePath(); g.fill(); }
      else g.fillRect(m.x - s, m.y - s, 2 * s, 2 * s);
    }
  }, [data, layers, worldState, markers, art, artLoads, hidden, colors, latest, ghost, ghostCells, target.waypoints, target.picked, focus]);
  const baseCache = useRef<{ key: string; paint: typeof paintBase; canvas: HTMLCanvasElement } | null>(null);
  const draw = useCallback(() => {
    const c = canvas.current; if (!c || !data) return;
    const dpr = window.devicePixelRatio || 1, w = c.clientWidth, h = c.clientHeight;
    if (c.width !== w * dpr || c.height !== h * dpr) { c.width = w * dpr; c.height = h * dpr; }
    const g = c.getContext("2d"); if (!g) return;
    const { ox, oy, scale } = view.current;
    const key = `${ox},${oy},${scale},${w},${h},${dpr}`;
    let cache = baseCache.current;
    if (!cache || cache.key !== key || cache.paint !== paintBase) {
      const off = cache?.canvas ?? document.createElement("canvas");
      off.width = c.width; off.height = c.height;
      const og = off.getContext("2d"); if (!og) return;
      paintBase(og, w, h, dpr);
      cache = baseCache.current = { key, paint: paintBase, canvas: off };
    }
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(cache.canvas, 0, 0);
    g.setTransform(dpr * scale, 0, 0, -dpr * scale, -ox * scale * dpr, oy * scale * dpr);
    const px = 1 / scale;
    if (sketch) {
      const [x0, y0, x1, y1] = sketch;
      g.fillStyle = "#ff3030"; g.globalAlpha = .28; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1;
      g.strokeStyle = "#ffffff"; g.lineWidth = 2 * px; g.setLineDash([6 * px, 4 * px]); g.strokeRect(x0, y0, x1 - x0, y1 - y0); g.setLineDash([]);
    }
    if (moved) {
      const [x0, y0, x1, y1] = moved.box, [o0, p0, o1, p1] = moved.goal.box;
      g.strokeStyle = "#3ddc97"; g.lineWidth = 1.5 * px; g.setLineDash([4 * px, 4 * px]); g.strokeRect(o0, p0, o1 - o0, p1 - p0);
      g.fillStyle = "#3ddc97"; g.globalAlpha = .35; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1;
      g.strokeStyle = "#ffffff"; g.lineWidth = 2 * px; g.setLineDash([6 * px, 4 * px]); g.strokeRect(x0, y0, x1 - x0, y1 - y0); g.setLineDash([]);
    }
    const hot = hotAvoid >= 0 ? data.avoid[hotAvoid] : undefined;
    if (hot) { g.strokeStyle = "#ffffff"; g.lineWidth = 3 * px; g.strokeRect(hot[0], hot[1], hot[2] - hot[0], hot[3] - hot[1]); }
    const z = hotZip >= 0 ? data.zips?.[hotZip] : undefined;
    if (z) {
      g.fillStyle = "#ffb74d"; g.globalAlpha = .18;
      for (const c of z.corridor) { g.beginPath(); g.moveTo(c[0], c[1]); for (let i = 2; i + 1 < c.length; i += 2) g.lineTo(c[i], c[i + 1]); g.closePath(); g.fill(); }
      g.globalAlpha = 1;
      const bw = z.box[2] - z.box[0], bh = z.box[3] - z.box[1];
      g.strokeStyle = "#ffb74d"; g.lineWidth = 2.5 * px; g.strokeRect(z.box[0], z.box[1], bw, bh);
      g.strokeStyle = "#ffffff"; g.lineWidth = 2 * px; g.beginPath(); g.moveTo(z.path[0][0], z.path[0][1]);
      for (const [x, y] of z.path.slice(1)) g.lineTo(x, y);
      g.stroke();
      g.strokeStyle = "#ffb74d"; g.lineWidth = 1.5 * px;
      for (const [x, y] of z.path.slice(1)) g.strokeRect(x - bw / 2, y - bh / 2, bw, bh);
    }
  }, [data, paintBase, sketch, moved, hotZip, hotAvoid]);
  useEffect(() => { draw(); }, [draw]);
  useEffect(() => { window.addEventListener("resize", draw); return () => window.removeEventListener("resize", draw); }, [draw]);
  useEffect(() => {
    const c = canvas.current; if (!c) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const v = view.current, rect = c.getBoundingClientRect(), mx = e.clientX - rect.left, my = e.clientY - rect.top;
      const wx = v.ox + mx / v.scale, wy = v.oy - my / v.scale, scale = v.scale * Math.exp(-e.deltaY * 0.0015);
      view.current = { scale, ox: wx - mx / scale, oy: wy + my / scale };
      draw();
    };
    c.addEventListener("wheel", onWheel, { passive: false });
    return () => c.removeEventListener("wheel", onWheel);
  }, [draw]);

  const world = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const v = view.current, rect = e.currentTarget.getBoundingClientRect();
    return { x: v.ox + (e.clientX - rect.left) / v.scale, y: v.oy - (e.clientY - rect.top) / v.scale };
  };
  const goalAt = (p: { x: number; y: number }, boxOf = (goal: MapGoal) => goal.box) => (data?.goals ?? [])
    .map(goal => ({ goal, box: boxOf(goal) }))
    .filter(({ box }) => p.x >= box[0] && p.x <= box[2] && p.y >= box[1] && p.y <= box[3])
    .sort((a, b) => (a.box[2] - a.box[0]) * (a.box[3] - a.box[1]) - (b.box[2] - b.box[0]) * (b.box[3] - b.box[1]))[0];
  const onDown = (e: React.MouseEvent<HTMLCanvasElement>) => {
    panned.current = false;
    if (moving) {
      const p = world(e), hit = goalAt(p, goal => moved?.goal.index === goal.index ? moved.box : goal.box);
      if (hit) { grab.current = { goal: hit.goal, box: hit.box, x: p.x, y: p.y }; setMoved({ goal: hit.goal, box: hit.box }); return; }
    }
    if (!drawing) { drag.current = { x: e.clientX, y: e.clientY }; return; }
    const p = world(e);
    sketchStart.current = p;
    setSketch([p.x, p.y, p.x, p.y]);
  };
  const onUp = () => {
    drag.current = null;
    grab.current = null;
    const start = sketchStart.current;
    if (!start) return;
    sketchStart.current = null;
    const s = sketch && Math.abs(sketch[2] - sketch[0]) > 1 && Math.abs(sketch[3] - sketch[1]) > 1 ? sketch : null;
    if (!data?.fulltas) { setDrawing(false); setSketch(s); return; }
    if (!s) { setSketch(null); const hit = goalAt(start); if (hit) setAvoidGoal(String(hit.goal.index - 1)); return; }
    if (drawBlock) { setSketch(null); setFailure(drawBlock); return; }
    const from = avoidOpen.length ? SIDES.filter(side => !avoidOpen.includes(side)).join("+") : "";
    const id = (k: number | null) => k == null || k < 0 ? "" : goalIds.current[k] ?? "";
    if (target.stretch != null) editAvoid(s, [target.fulltas!], false, { goal: target.stretch, from });
    else editAvoid(s, sketchRuns, false, { goal: avoidScope === "only" ? id(focus) : "", after: avoidScope === "from" && focus ? id(focus - 1) : "", from });
  };
  const onMove = (e: React.MouseEvent<HTMLCanvasElement>) => {
    const v = view.current;
    if (drag.current) { v.ox -= (e.clientX - drag.current.x) / v.scale; v.oy += (e.clientY - drag.current.y) / v.scale; drag.current = { x: e.clientX, y: e.clientY }; panned.current = true; draw(); }
    const { x, y } = world(e), reach = 10 / v.scale;
    if (sketchStart.current) setSketch([sketchStart.current.x, sketchStart.current.y, x, y]);
    const held = grab.current;
    if (held) { const dx = x - held.x, dy = y - held.y; setMoved({ goal: held.goal, box: [held.box[0] + dx, held.box[1] + dy, held.box[2] + dx, held.box[3] + dy] }); }
    const near = layers.markers ? markers.find(m => Math.abs(m.x - x) < reach && Math.abs(m.y - y) < reach) : undefined;
    const size = data?.explored.cell ?? 1;
    const cell = `${Math.floor(x / size)},${Math.floor(y / size)}`;
    const zipAt = (data?.zips ?? []).findIndex(z => inState(z.when, worldState) && x >= z.box[0] && x <= z.box[2] && y >= z.box[1] && y <= z.box[3]);
    if (zipAt !== hotZip) setHotZip(zipAt);
    setCursor({ x, y, marker: near ? (near.box ? boxTitle(near) : `${near.name} (${near.kind})`) : undefined, reached: arrivals.get(cell), delta: ghostCells?.deltaAt.get(cell) });
  };
  const onClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (panned.current) return;
    const p = world(e), reach = 10 / view.current.scale;
    let near: Marker | null = null, best = reach;
    for (const m of markers as Marker[]) {
      if (m.kind !== "box" || !layers[`box:${m.box!.category}`]) continue;
      const gap = Math.hypot(m.x - p.x, m.y - p.y);
      if (gap < best) { best = gap; near = m; }
    }
    if (near) { setBoxTip(near); setCursor({ x: p.x, y: p.y, marker: boxTitle(near) }); }
  };
  const editAvoid = async (box: number[], names: string[], remove: boolean, when: { goal?: string; after?: string; from?: string } = {}) => {
    setSaving(true); setFailure("");
    try { await request("/fulltas/avoid", { box: box.slice(0, 4), runs: names, remove, ...when }); if (!remove) setSketch(null); await load(); }
    catch (e) { setFailure(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };
  const editCourseAvoid = async (box: number[], name: string, on: boolean, remove: boolean) => {
    setSaving(true); setFailure("");
    try { await request("/enginesim/avoid", { course: target.course, capabilities: target.capabilities, box, name, on, remove }); if (!remove) setSketch(null); await load(); }
    catch (e) { setFailure(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };
  const center = (b: number[]) => ({ x: Math.round((b[0] + b[2]) / 2 * 10) / 10, y: Math.round((b[1] + b[3]) / 2 * 10) / 10 });
  const placeMoved = (x: number, y: number) => setMoved(m => {
    if (!m) return m;
    const w = m.box[2] - m.box[0], h = m.box[3] - m.box[1];
    return { goal: m.goal, box: [x - w / 2, y - h / 2, x + w / 2, y + h / 2] };
  });
  const saveMoved = async () => {
    if (!moved || !data?.fulltas) return;
    setSaving(true); setFailure("");
    try { const to = center(moved.box); await request("/fulltas/move-goal", { name: data.fulltas, goal: moved.goal.id, label: moved.goal.label, x: to.x, y: to.y }); setMoved(null); await load(); }
    catch (e) { setFailure(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };
  const boxText = (b: number[]) => `${Math.min(b[0], b[2]).toFixed(1)}, ${Math.min(b[1], b[3]).toFixed(1)} to ${Math.max(b[0], b[2]).toFixed(1)}, ${Math.max(b[1], b[3]).toFixed(1)}`;
  const toggle = (key: keyof MapLayers) => setLayers(l => ({ ...l, [key]: !l[key] }));
  return <div className="panel offlinePanel solveMap"><div className="panelTitle"><div><p className="eyebrow">SEARCH MAP</p><h2>{target.fulltas ? `Full-TAS · ${target.fulltas}` : <>{courseLabel(courses.find(c => c.id === target.course), target.course)} · {target.capabilities}{target.optional.map(n => ` +${n}`).join("")}</>}</h2></div>
    <div className="buttonRow"><button className="secondary" disabled={loading} onClick={() => load()}>{loading ? `Drawing…${progress}` : "Redraw"}</button><button className="secondary" disabled={!data} onClick={() => { if (data) { fit(data); draw(); } }}>Fit</button><button className="secondary" onClick={onClose}>Close</button></div></div>
    {target.fulltas
      ? <><p className="lede">Shows this run&apos;s search windows, best route, goals and avoid boxes.</p><details className="howTo"><summary>How it works</summary><ul><li>Color: when a window first arrived; blue early, red late.</li><li>Gold: best so far, replayed from the run&apos;s start.</li><li>Green boxes: <code>at:</code> goals; red: avoid boxes.</li><li>Avoid boxes come from forced-paths.json rules naming this run.</li><li>Draw avoid: set when a box cuts and its open sides, then drag boxes; each is saved as drawn. Click a goal&apos;s box to pick its number.</li><li>A one-way box&apos;s open sides are dashed green: runs may enter there.</li><li>Boxes that don&apos;t cut at the picked goal are faded; the gold box is that goal.</li><li>Move goals drags goal boxes.</li><li>Wheel zooms, drag pans; coordinates match forced-paths.json and <code>at:</code>.</li></ul></details></>
      : <><p className="lede">The solver&apos;s level, explored area, saved routes and forced-path boxes.</p><details className="howTo"><summary>How it works</summary><ul><li>Color: fastest time reaching each spot; blue early, red late.</li><li>Grey: reached only with the timer off.</li><li>Gold: fastest offline best. Ring: a route&apos;s timer start.</li><li>Red boxes cut runs; dashed boxes are optional and off.</li><li>Numbered green boxes: Improve&apos;s waypoints; gold: the picked ones.</li><li>Draw avoid drags a cut box; name it for a block.</li><li>Ghost compare shows two routes&apos; divergence and arrival deltas.</li></ul></details></>}
    {failure && <p className="mapFailure">{failure}</p>}
    <div className="mapLayers">
      {([["art", "Game art"], ["background", "Background art"], ["tree", "Tree"], ["shapes", "Collision shapes"], ["explored", "Explored"], ["boxes", "Forced-path boxes"], ["markers", "Gates, checkpoints, teleporters, refills"],
        ...Object.entries(BOX_KINDS).map(([k, m]) => [`box:${k}`, `Buy box: ${m.text}`] as [keyof MapLayers, string])] as [keyof MapLayers, string][]).filter(([key]) => art || (key !== "art" && key !== "background" && key !== "tree" && key !== "shapes")).map(([key, label]) =>
        <label key={key} className="passLoop"><input type="checkbox" checked={layers[key]} onChange={() => toggle(key)}/>{label}</label>)}
      {data?.states && <StatePicker states={data.states} pick={worldState} onPick={setWorldState} className="passLoop"/>}
      {data && <span className="hint">{data.fulltas ? `${data.explored.cells.length.toLocaleString()} explored cells` : `${data.explored.prefixes.toLocaleString()} explore prefixes · ${data.explored.runs.toLocaleString()} runs`}</span>}
      {data && <span className="hint">{data.fulltas ? "run time" : "timer"} at first arrival: <span style={{ display: "inline-block", width: 90, height: 8, verticalAlign: "middle", background: "linear-gradient(90deg,hsl(230,90%,55%),hsl(172,90%,55%),hsl(115,90%,55%),hsl(57,90%,55%),hsl(0,90%,55%))" }}/> 0–{latest.toFixed(1)}s{!data.fulltas && <> · <span style={{ display: "inline-block", width: 8, height: 8, background: "#34383f", verticalAlign: "middle" }}/> timer off</>}</span>}
      {data && !data.fulltas && <button className={drawing ? "primary" : "secondary"} disabled={saving} title="Drag a box: search runs entering it are cut." onClick={() => { setDrawing(d => !d); setSketch(null); setMoving(false); }}>{drawing ? "Drag a box…" : "Draw avoid"}</button>}
      {data?.fulltas && <button className={moving ? "primary" : "secondary"} disabled={saving} title="Drag a goal's green box; it keeps its size." onClick={() => { setMoving(m => !m); setMoved(null); setDrawing(false); setSketch(null); }}>{moving ? "Drag a goal…" : "Move goals"}</button>}
    </div>
    <div className="ghostBar">
      <strong>Ghost compare</strong>
      <label className="ghostPick">A
        <select value={ghostA} onChange={e => setGhostA(e.target.value)}>
          <option value="">pick a route</option>
          {data?.routes.map(r => <option key={r.name} value={r.name}>{r.name}</option>)}
        </select>
      </label>
      <label className="ghostPick">B
        <select value={ghostB} onChange={e => setGhostB(e.target.value)}>
          <option value="">pick a route</option>
          {data?.routes.map(r => <option key={r.name} value={r.name}>{r.name}</option>)}
        </select>
      </label>
      {ghostA && ghostA === ghostB && <span className="hint">Pick two different routes.</span>}
      {ghostLoading && <span className="hint">Comparing…</span>}
      {ghost && ghostCells && <span className="hint">
        <span className="mapSwatch" style={{ background: GHOST_A }} />A
        <span className="mapSwatch" style={{ background: `repeating-linear-gradient(90deg, ${GHOST_B} 0 6px, transparent 6px 10px)` }} />B
        {" · "}{ghost.diverge ? <>states first diverge at tick {ghost.diverge.tick}</> : <>same states all the way ({ghost.a.state.length} ticks)</>}
        {ghostCells.onlyA.length + ghostCells.onlyB.length > 0 && <> · {ghostCells.onlyA.length + ghostCells.onlyB.length} cells on one path only</>}
        {" · "}{ghostCells.both.length > 0
          ? <>Δ first arrival (B − A): <span style={{ display: "inline-block", width: 90, height: 8, verticalAlign: "middle", background: "linear-gradient(90deg,#48b9ff,#8899aa,#ff5b6e)" }}/> {ghostCells.low.toFixed(2)}…{ghostCells.high.toFixed(2)}s</>
          : <>no shared cells with both timers running</>}
      </span>}
    </div>
    {ghostError && <p className="mapFailure">{ghostError}</p>}
    {data?.fulltas && <div className="ghostBar">
      <strong>Avoid boxes</strong>
      <button className={drawing ? "primary" : "secondary"} title="Drag boxes on the map: each is saved as drawn, with these settings." onClick={() => { setDrawing(d => !d); setSketch(null); setMoving(false); setMoved(null); }}>{drawing ? "Stop drawing" : "Draw avoid"}</button>
      {target.stretch != null ? <code className="hint">Cuts only on this stretch: heading for goal {stretchAt}, {goals?.[stretchAt]}</code> : <><label className="ghostPick">Cuts
        <select value={avoidScope} onChange={e => setAvoidScope(e.target.value as typeof avoidScope)}>
          <option value="every">for every goal</option>
          <option value="only">only heading for goal</option>
          <option value="from">from goal on</option>
        </select>
      </label>
      <label className="ghostPick" title="Its number as the goals table shows it. Also fades the boxes that don't cut there.">Goal
        <input type="number" min={0} style={{ width: 80 }} placeholder="any" value={avoidGoal} onChange={e => setAvoidGoal(e.target.value)}/>
      </label>
      {focus != null && <code className="hint">{goals?.[focus] ?? "no such goal"}</code>}</>}
      <span className="ghostPick" title="Runs may enter the box across these sides; it cuts entering across the rest. None: any tick inside is cut.">Open sides
        {SIDES.map(side => <label key={side} className="passLoop"><input type="checkbox" checked={avoidOpen.includes(side)} onChange={() => setAvoidOpen(o => o.includes(side) ? o.filter(k => k !== side) : [...o, side])}/>{side}</label>)}
      </span>
      {(runs?.length ?? 0) > 1 && <span className="ghostPick">Runs
        {runs!.map(n => <label key={n} className="passLoop"><input type="checkbox" checked={sketchRuns.includes(n)} onChange={() => setSketchRuns(r => r.includes(n) ? r.filter(k => k !== n) : [...r, n])}/>{n}</label>)}
      </span>}
      {drawing && drawBlock && <span className="hint">{drawBlock}</span>}
    </div>}
    <canvas ref={canvas} className="mapCanvas" style={drawing ? { cursor: "crosshair" } : moving ? { cursor: "move" } : undefined} onMouseDown={onDown} onMouseUp={onUp} onMouseLeave={() => { onUp(); setCursor(null); }} onMouseMove={onMove} onClick={onClick}/>
    {boxTip?.box && <div className="mapReadout"><strong>{boxTip.name}</strong> · {BOX_KINDS[boxTip.box.category].text} · gives {boxTip.box.gives} · {boxTip.box.upgrade} · {boxCost(boxTip.box)} · {boxTip.box.cap === 1 ? "one-off" : `repeatable ×${boxTip.box.cap}`} · {boxTip.box.secret ? `secret${boxTip.box.number ? ` #${boxTip.box.number}` : ""} · ` : ""}{boxTip.box.ach ? `${boxTip.box.ach} · ` : ""}{boxTip.box.tree ? "tree upgrade · " : ""}{boxTip.box.fresh ? "in the fresh world" : "only later"}
      <button className="secondary" onClick={() => setBoxTip(null)}>×</button></div>}
    <div className="mapReadout">{cursor ? <>world {cursor.x.toFixed(1)}, {cursor.y.toFixed(1)}{cursor.reached != null && <> · {cursor.reached < 0 ? "reached only with the timer off" : `first reach ${cursor.reached.toFixed(2)}s`}</>}{cursor.delta != null && <> · Δ {cursor.delta > 0 ? "+" : ""}{cursor.delta.toFixed(2)}s (B − A)</>}{cursor.marker && <> · <strong>{cursor.marker}</strong></>}</> : "Point at the map for coordinates."}</div>
    {sketch && !sketchStart.current && !data?.fulltas && <div className="buttonRow">
      <span className="hint">Avoid {boxText(sketch)} for</span>
      <input placeholder="block name (optional)" value={sketchName} onChange={e => setSketchName(e.target.value)} title="Empty: cut for every solve. Named: a pickable optional block."/>
      <label className="passLoop" title="Starts on for solves that pick no blocks."><input type="checkbox" disabled={!sketchName.trim()} checked={sketchOn} onChange={e => setSketchOn(e.target.checked)}/>on by default</label>
      <button className="primary" disabled={saving} onClick={() => editCourseAvoid(sketch, sketchName.trim(), sketchOn, false)}>Add avoid</button>
      <button className="secondary" disabled={saving} onClick={() => setSketch(null)}>Discard</button></div>}
    {moved && <div className="buttonRow">
      <span className="hint">Goal {moved.goal.index - 1} <code>{moved.goal.label}</code>: centre {center(moved.goal.box).x}, {center(moved.goal.box).y} to</span>
      <Field label="x"><input type="number" step="0.1" value={center(moved.box).x} onChange={e => placeMoved(Number(e.target.value), center(moved.box).y)}/></Field>
      <Field label="y"><input type="number" step="0.1" value={center(moved.box).y} onChange={e => placeMoved(center(moved.box).x, Number(e.target.value))}/></Field>
      <button className="primary" disabled={saving || busy} title={busy ? "Stop solving or playing first." : "Saves the goal in runs/<run>.json."} onClick={saveMoved}>Save goal</button>
      <button className="secondary" disabled={saving} onClick={() => setMoved(null)}>Discard</button></div>}
    {data?.fulltas && data.avoid.length > 0 && <><span className="hint">{data.avoid.length} avoid boxes{focus != null && ` · ${data.avoid.filter(b => avoidLive(b, focus)).length} cut at goal ${focus}, listed first`} · hover one to find it</span>
      <ul className="mapRoutes" style={{ maxHeight: 220, overflowY: "auto" }}>{data.avoid.map((b, i) => ({ b, i, live: focus == null || avoidLive(b, focus) }))
        .sort((p, q) => Number(q.live) - Number(p.live) || (p.b[4] ?? 0) - (q.b[4] ?? 0)).map(({ b, i, live }) =>
        <li key={i} style={live ? undefined : { opacity: .45 }} onMouseEnter={() => setHotAvoid(i)} onMouseLeave={() => setHotAvoid(-1)}><span className="mapSwatch" style={{ background: "#ff3030" }}/>{avoidText(b)} · {boxText(b)}
          <button className="secondary" disabled={saving} title="Removes this box; shared boxes go for other runs too." onClick={() => { setHotAvoid(-1); editAvoid(b, [data.fulltas!], true); }}>Remove</button></li>)}</ul></>}
    {data && !data.fulltas && (data.avoid.length > 0 || (data.optional ?? []).some(b => b.avoid.length + b.via.length > 0)) && <ul className="mapRoutes">
      {data.avoid.map((b, i) => <li key={`standing${i}`}><span className="mapSwatch" style={{ background: "#ff3030" }}/>avoid {boxText(b)}
        <button className="secondary" disabled={saving} title="Removes this box; shared boxes go for other maps too." onClick={() => editCourseAvoid(b, "", false, true)}>Remove</button></li>)}
      {(data.optional ?? []).flatMap(block => [...block.avoid.map(b => ({ b, via: false })), ...block.via.map(b => ({ b, via: true }))].map(({ b, via }, j) =>
        <li key={`${block.name}${via ? "v" : "a"}${j}`}><span className="mapSwatch" style={{ background: via ? "#3ddc97" : "#ff3030" }}/><code>{block.name}</code> {via ? "via" : "avoid"} {boxText(b)}
          <button className="secondary" disabled={saving} title="Removes this box; the block drops when empty." onClick={() => editCourseAvoid(b, "", false, true)}>Remove</button></li>))}
    </ul>}
    {data && data.routes.length > 0 && <ul className="mapRoutes">{data.routes.map((r, i) => <li key={i}><label className="passLoop"><input type="checkbox" checked={!hidden.has(i)} onChange={() => setHidden(h => { const n = new Set(h); if (n.has(i)) n.delete(i); else n.add(i); return n; })}/>
      <span className="mapSwatch" style={{ background: colors[i] }}/>{r.name}<small>{r.finished ? `${r.time.toFixed(2)}s` : "no finish"} · {r.points.length / 2} ticks</small></label></li>)}</ul>}
  </div>;
}

type Passes = { minutes: number; maxMinutes: number; repeat: boolean };
function usePasses(): [Passes, (update: Partial<Passes>) => void] {
  const [passes, setPasses] = useState<Passes>({ minutes: 1, maxMinutes: 15, repeat: true });
  useEffect(() => {
    try { const saved = JSON.parse(window.localStorage.getItem("optimizePasses") ?? "null"); if (saved) setPasses(p => ({ ...p, ...saved })); }
    catch { }
  }, []);
  const update = (u: Partial<Passes>) => setPasses(p => { const next = { ...p, ...u }; window.localStorage.setItem("optimizePasses", JSON.stringify(next)); return next; });
  return [passes, update];
}

const minutesText = (m: number) => m < 1 ? `${Math.round(m * 60)} s` : `${+m.toFixed(2)} min`;

function PassFields({passes,update,disabled}:{passes:Passes;update:(u:Partial<Passes>)=>void;disabled:boolean}) {
  const clamp = (v: string) => Math.max(0.25, Math.round((Number(v) || 0.25) * 4) / 4);
  return <div className="passFields">
    <Field label={passes.repeat ? "Minutes per target, pass 1" : "Minutes per target"}><input type="number" min={0.25} step={0.25} disabled={disabled} value={passes.minutes} onChange={e => update({ minutes: clamp(e.target.value) })} /></Field>
    <label className="passLoop" title="Loops targets with double minutes per pass, up to max."><input type="checkbox" disabled={disabled} checked={passes.repeat} onChange={e => update({ repeat: e.target.checked })} /><span>Loop until stopped</span></label>
    <Field label="Max minutes per target"><input type="number" min={0.25} step={0.25} disabled={disabled || !passes.repeat} value={passes.maxMinutes} onChange={e => update({ maxMinutes: clamp(e.target.value) })} /></Field>
  </div>;
}

function progressText(solver: SolverState) {
  const queue = solver.queue ?? [], at = queue.findIndex(q => q.active);
  return [solver.repeat ? `Pass ${solver.pass ?? 1}` : "", at >= 0 && queue.length > 1 ? `target ${at + 1} of ${queue.length}` : "", solver.minutes ? `${minutesText(solver.minutes)} each` : ""].filter(Boolean).join(" · ");
}

function SolveMany({solver,courses,categories,routes,targets,onTargets,onCommand}:{solver:SolverState;courses:Course[];categories:Category[];routes:Route[];targets:CourseTarget[];onTargets:(t:CourseTarget[])=>void;onCommand:(path:string,body?:unknown)=>Promise<boolean>}) {
  const [passes, updatePasses] = usePasses();
  const bests = categoryBests(routes, courses, categories);
  const rows = targets.map(t => {
    const c = courses.find(x => x.id === t.course), cat = categories.find(x => x.id === t.capabilities);
    const lapless = !!c && (c.bonus || !!c.to);
    const b = !c || !cat ? undefined : t.class ? soloBest(bests.human[t.class], c, cat, categories)
      : t.farm && !lapless ? soloBest(bests.farm, c, cat, categories) : directionBest(bests, c, cat, categories, t.direction);
    const optional = t.direction ? [...(c?.optional ?? []).filter(o => o.on && o.capabilities.includes(t.capabilities) && !Object.values(DIRECTION_BLOCK).includes(o.name)).map(o => o.name), DIRECTION_BLOCK[t.direction]] : undefined;
    return { t, label: targetLabel(t, courses, categories),
      time: b ? `${t.class ? "score " : ""}${b.time.toFixed(t.farm && !lapless ? 4 : 2)}` : "no route yet",
      job: { course: t.course, capabilities: t.capabilities, farm: t.farm && !lapless, ...optional ? { optional } : {}, ...t.class ? { class: t.class } : {} } };
  });
  const progress = solver.queue ?? [];
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">OFFLINE SEARCH</p><h2>Improve {targets.length} targets</h2></div><span className={`offlineStatus ${solver.running ? "running" : ""}`}>{solver.running ? progressText(solver) || "Improving" : "Idle"}</span></div>
    <p className="lede">Each target improves in turn; a target without a route gets a first solve. Click a slot above to open one alone.</p>
    <ul className="manyTargets">{rows.map((r, i) => <li key={i}><span>{r.label}</span><small>{r.time}</small>
      <button className="secondary" disabled={solver.running} title="Drop it from the targets" onClick={() => onTargets(targets.filter((_, k) => k !== i))}>✕</button></li>)}</ul>
    <PassFields passes={passes} update={updatePasses} disabled={solver.running} />
    <div className="buttonRow">{solver.running
      ? <><button className="secondary" onClick={() => onCommand("/solver/skip")}>Skip current target</button><button className="danger" onClick={() => onCommand("/solver/stop")}>Stop all</button></>
      : <button className="primary" onClick={() => onCommand("/solver/start", { queue: rows.map(r => r.job), ...passes })}>Improve {rows.length} targets{passes.repeat ? " · loop until stopped" : ` · up to ${minutesText(rows.length * passes.minutes)}`}</button>}</div>
    {progress.length > 1 && <ol className="matrixQueue">{progress.map((q, i) => <li key={i} className={q.active ? "active" : q.result ? "done" : ""}><strong>{q.label}</strong><span>{q.active ? "Improving…" : q.result ?? "Waiting"}</span></li>)}</ol>}
    <p className="offlineConfirmation">{solver.status}</p>
  </div>;
}

const TICK_SECONDS = 0.02;
const tickText = (ticks: number) => `${ticks} t · ${runTime(ticks * TICK_SECONDS)}`;
const liveText = (seconds: number, ticks: number) => `${runTime(seconds)} · ${ticks} f`;

function FullTas({fullTas,running,onCommand,onMap}:{fullTas?:FullTasState;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(name:string)=>void}) {
  const runs = fullTas?.runs ?? [];
  const [selected, setSelected] = useState("");
  const [newName, setNewName] = useState("");
  const [picked, setPicked] = useState<[number, number] | null>(null);
  const name = selected || runs[0]?.name || "";
  const run = runs.find(r => r.name === name);
  const savedGoals = (run?.goals ?? []).map(g => g.label).join("\n");
  const [draft, setDraft] = useState(savedGoals);
  useEffect(() => setDraft(savedGoals), [name, savedGoals]);
  useEffect(() => setPicked(null), [name]);
  const [fromFrame, setFromFrame] = useState("");
  const splitTicks = (run?.best?.splits ?? run?.offline?.splits ?? []).map(s => s.ticks);
  useEffect(() => { if (picked) setFromFrame(String(picked[0] > 1 ? splitTicks[picked[0] - 2] ?? "" : 0)); }, [picked]);
  if (!fullTas) return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className="offlineStatus">Unavailable</span></div>
    <p className="lede">The game plugin doesn&apos;t report Full-TAS runs.</p></div>;
  const solve: FullTasSolveState = fullTas.solve ?? { running: false, status: "", log: [] };
  const busy = solve.running || running;
  const goals = draft.split("\n").map(g => g.trim()).filter(g => g);
  const validName = /^[A-Za-z0-9%-]+$/.test(newName);
  const splits = run?.best?.splits ?? run?.offline?.splits ?? [];
  const labels = (run?.goals ?? []).map(g => g.label), ids = (run?.goals ?? []).map(g => g.id);
  const segments = resetSegments(run?.offline?.segments);
  const idleAt = new Map((run?.offline?.idles && (!run.best || run.best.splits.every((s, i) => run.offline?.splits[i]?.ticks === s.ticks)) ? run.offline.idles : []).map(d => [ids.indexOf(d.id) + 1, d]));
  const breakerAt = run?.breaker ? ids.indexOf(run.breaker) : -1;
  const complete = labels.length > 0 && splits.length === labels.length;
  const reached = splits.length;
  const pick = (first: number, last: number, shift: boolean) => setPicked(p => shift && p ? [Math.min(p[0], first), Math.max(p[1], last)]
    : p && p[0] === first && p[1] === last ? null : [first, last]);
  const pickedSeconds = picked && picked[1] <= reached ? liveSeconds(splits[picked[1] - 1]) - (picked[0] > 1 ? liveSeconds(splits[picked[0] - 2]) : 0) : null;
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className={`offlineStatus ${busy?"running":""}`}>{solve.running?"Improving":running?"Playing":"Idle"}</span></div>
    <p className="lede">The whole game as one input stream, timed to the last goal.</p><details className="howTo"><summary>Goal syntax</summary><ul><li>One goal per line; reached in order.</li><li><code>record:1</code> a lap of Course 1: its start gate, then its finish · <code>farm:1</code> its finish (a lap from a quick restart or the end gate's drop) · <code>start:1</code> its start gate alone</li><li><code>buy:&lt;box&gt;</code> · <code>reward:bonusN</code> · <code>reward:&lt;name&gt;</code> (a named courses.json bonus)</li><li><code>at:x,y,w,h</code> area goal, any entry side.</li><li><code>at:x,y,w,h,from=left+bottom</code> limits entry sides.</li><li><code>"start": "vman"</code> in runs/&lt;run&gt;.json: Vman world.</li></ul></details><details className="howTo"><summary>How improving works</summary><ol><li><b>Improve</b> searches offline from the run&apos;s record and picks where to look itself.</li><li>Think a stretch is slow? Click its goals or segment bar (shift-click extends), then Improve.</li><li>Improve also edits the economy goals itself: it drops laps the Cash no longer needs, laps where the route stood waiting, and swaps in a record lap when that pays; an edit stays only when the whole run is faster. The game saves the new goal list with the record (runs/&lt;run&gt;.json backed up first).</li><li>The game replays every faster route in turbo from its start.</li><li>Runs save only when complete, held up and faster.</li><li>Goals split the run; nothing restarts between them.</li><li>A quick restart after an area goal takes it back.</li></ol></details>
    {([["complete", "Complete"], ["wip", "WIP"], ["test", "Tests"]] as const).map(([section, title]) => <div key={section}><h3 className="fullTasSection">{title}</h3><div className="fullTasRuns">
      {runs.filter(r => r.section === section).map(r => <button key={r.name} className={`fullTasRun ${r.name===name?"selected":""}`} onClick={() => setSelected(r.name)}>
        <strong>{r.name}</strong><small>{r.start === "vman" ? "Vman · " : ""}{r.savestate ? "Savestate · " : ""}{r.goals.length} goal{r.goals.length===1?"":"s"}</small>
        {r.best?<b>{liveText(r.best.time, r.best.splits[r.best.splits.length - 1]?.ticks ?? 0)}</b>:r.offline?<b className="none">offline {r.offline.reached}/{r.goals.length} · {runTime(r.offline.time)}</b>:<b className="none">no route yet</b>}
        {!r.traceReady && <span className="badge missing">No trace</span>}
      </button>)}
      {section === "wip" && name && !run && <div className="fullTasRun selected unsaved"><strong>{name}</strong><small>New · save its goals to create it</small></div>}
    </div></div>)}
    {!name && <p className="offlineConfirmation">No Full-TAS run yet: name a new one.</p>}
    <div className="fullTasInline">
      <Field label="New run (letters, digits, %, -)"><input value={newName} placeholder="main" onChange={e => setNewName(e.target.value)}/></Field>
      <button className="secondary" disabled={!validName || runs.some(r => r.name === newName)} onClick={() => { setSelected(newName); setNewName(""); }}>+ New run</button>
    </div>
    {name && <div className="fullTasGrid">
      <div className="fullTasGoals">
        <div className="panelTitle"><div><p className="eyebrow">{run?.best?run.best.name:run?.offline?`OFFLINE BEST · ${run.offline.reached}/${run.goals.length} GOALS · UNSAVED`:"NO ROUTE YET"}</p><h2>{name}{run?.best?` · ${liveText(run.best.time, run.best.splits[run.best.splits.length - 1]?.ticks ?? 0)} live`:run?.offline?` · ${runTime(run.offline.time)} to goal ${run.offline.reached - 1}`:""}</h2></div></div>
        <div className="fullTasSolve">
          {reached > 0 && <div className={`ftFocus ${picked ? "on" : ""}`}>
            {picked
              ? <><span>Selected: <b>{goalSpan(picked)}</b> ({picked[1] - picked[0] + 1} goal{picked[1] > picked[0] ? "s" : ""}, {pickedSeconds?.toFixed(2)} s in the record){picked[0] !== picked[1] ? ". Pick one goal to search its way again." : ""}</span>
                <button className="secondary" onClick={() => setPicked(null)}>Clear selection</button></>
              : <span>Solve builds what the best doesn't reach yet, then improves the whole run. Think one goal's way is slow? Click it below and search it again.</span>}
          </div>}
          <div className="ftImprove">
            <button className="primary" disabled={busy || !run?.goals.length} title="Builds whatever the best doesn't reach yet, then improves the whole run until it converges or Stop."
              onClick={() => onCommand("/fulltas/solve", { name })}>Solve</button>
            {picked && picked[0] === picked[1] && <button className="secondary" disabled={busy} title={`Searches the way to ${goalSpan(picked)} again from where the run starts it, longer; kept when the whole run gets faster.`}
              onClick={() => onCommand("/fulltas/solve", { name, stretch: ids[picked[0] - 1] })}>Search {goalSpan(picked)} again</button>}
            <button className="danger" disabled={!busy} onClick={() => onCommand("/fulltas/stop")}>Stop</button>
          </div>
          <div className="buttonRow">
            <button className="secondary" disabled={busy || !run} title="Starts a trace and world capture from a fresh game." onClick={() => onCommand("/fulltas/capture", { name })}>Capture start</button>
            <button className="secondary" disabled={busy || !(run?.playable ?? run?.best)} title={run?.best ? "Plays the saved best in real time from a new game." : "Plays the offline best so far; stops at its last goal."} onClick={() => onCommand("/fulltas/play", { name })}>Play</button>
            <span className="inlineInput"><input type="number" min="0" value={fromFrame} placeholder="frame" title="Frame to skip to in turbo; picking a goal fills in its start" onChange={e => setFromFrame(e.target.value)}/></span>
            <button className="secondary" disabled={busy || !(run?.playable ?? run?.best) || !(Number(fromFrame) > 0)} title={`Skips to frame ${fromFrame} in turbo, then plays at 1x`} onClick={() => onCommand("/fulltas/play", { name, fromTick: Number(fromFrame) })}>▶ Play from {fromFrame || "…"}</button>
            <button className="secondary" disabled={running || !run?.closest} title={run?.closest ? `Plays the closest attempt (${run.closest.reached}/${run.goals.length} goals, ${tickText(run.closest.ticks)}).` : "No attempt yet; Reach missing goals creates one."} onClick={() => onCommand("/fulltas/play", { name, closest: true })}>Test best</button>
            <button className="secondary" disabled={!run?.traceReady} title="Draws search windows, best route, goals and avoid boxes." onClick={() => onMap(name)}>Show map</button>
            <label className={`capabilityLock executable ${run && fullTas.goalsOverlay === name ? "on" : ""}`} title="In game: shows this run's goal boxes; reached ones turn green.">
              <input type="checkbox" checked={!!run && fullTas.goalsOverlay === name} disabled={!run} onChange={e => onCommand("/settings/fulltas-goals", { name: e.target.checked ? name : "" })} />
              <strong>Goals in game</strong>
            </label>
          </div>
        </div>
        <div className={`resultTable ${segments.length > 1 ? "segmented" : ""}`}>
          <div className="tableHead"><span>#</span><span>Goal</span><span>Split (live · frames)</span><span>Cumulative (live · frames)</span></div>
          {(() => {
            const goalRow = (i: number) => { const label = labels[i]; const s = splits[i]; const b = breakerAt >= 0 ? breakerAt : null; const inPick = picked != null && i + 1 >= picked[0] && i + 1 <= picked[1];
              const title = [b!=null&&i===b?"Trips the breaker: the lights go out":"", goalTitle(label) ?? "", i < reached ? "Click to improve just this goal; shift-click to extend" : ""].filter(Boolean).join("\n");
              const idle = idleAt.get(i + 1);
              return <Fragment key={i}>
              {idle && <div className="tableRow idleRow" title={`The run waits at the box for ${label}'s Cash (part of goal ${i}'s split): standing there from frame ${idle.arrive[0]} until the Cash is there at frame ${idle.afford[0]}.`}>
                <span></span><span>idle · waiting for {label}</span>
                <span>{liveText(idle.afford[0] * 0.02 + idle.afford[1] / 280 - (idle.arrive[0] * 0.02 + idle.arrive[1] / 280), idle.afford[0] - idle.arrive[0])}</span><span></span>
              </div>}
              <div className={`tableRow ${s?"":"pending"} ${b==null||i<b?"":i===b?"breakerTrip":"breakerOut"} ${i < reached ? "pickable" : ""} ${inPick ? "picked" : ""}`} title={title || undefined}
                onClick={i < reached ? e => pick(i + 1, i + 1, e.shiftKey) : undefined}>
              <span>{i}</span><span>{label}</span>
              <span>{s ? liveText(liveSeconds(s) - (i > 0 ? liveSeconds(splits[i - 1]) : 0), s.ticks - (i > 0 ? splits[i - 1].ticks : 0)) : "—"}</span>
              <span>{s ? liveText(liveSeconds(s), s.ticks) : "—"}</span>
            </div></Fragment>; };
            if (segments.length <= 1) return labels.map((_, i) => goalRow(i));
            const lastBuy = labels.reduce((at, label, i) => label.startsWith("buy:") ? i : at, -1);
            const rows: ReactElement[] = segments.map((r, k) => {
              const goals: ReactElement[] = [];
              for (let i = r.first; i < r.last && i < labels.length; i++) goals.push(goalRow(i));
              return <SegmentGroup key={`segment${r.n}`} r={r} end={k === segments.length - 1} alt={k % 2 === 1} safe={r.first > lastBuy} selected={picked != null && picked[0] === r.first + 1 && picked[1] === r.last} onPick={shift => pick(r.first + 1, r.last, shift)} disabled={r.last > reached}>{goals}</SegmentGroup>;
            });
            for (let i = segments[segments.length - 1].last; i < labels.length; i++) rows.push(goalRow(i));
            return rows;
          })()}
          {!labels.length && <div className="tableRow pending"><span>—</span><span>No goals</span></div>}
        </div>
        {segments.length > 1 && <SegmentLegend />}
      </div>
      <div className="fullTasEditor">
        <Field label="Goals (one token per line)"><textarea value={draft} spellCheck={false} placeholder={"record:1\nbuy:buyreward"} onChange={e => setDraft(e.target.value)}/></Field>
        <div className="buttonRow"><button className="primary" disabled={busy || !goals.length || draft === savedGoals} onClick={() => onCommand("/fulltas/goals", { name, goals })}>Save goals</button>
          <button className="secondary" disabled={draft === savedGoals} onClick={() => setDraft(savedGoals)}>Revert</button></div>
      </div>
    </div>}
    {run?.classes && run.human && run.candidates && <FullTasClasses key={name} run={{ name, classes: run.classes, human: run.human, candidates: run.candidates }} queue={fullTas.queue ?? null} difficulty={fullTas.difficulty ?? null} busy={busy} complete={complete} onCommand={onCommand}/>}
    <div className="fullTasSearch">
      <div className="panelTitle"><div><p className="eyebrow">OFFLINE SEARCH</p><h2>{solve.name ? `Improve · ${solve.name}` : "Improve"}</h2></div><span className={`offlineStatus ${solve.running?"running":""}`}>{solve.running?"Running":"Idle"}</span></div>
      <FullTasSolvePanel solve={solve} goals={run?.goals ?? []} route={run ? { progress: splits.length, time: splits.length ? liveSeconds(splits[splits.length - 1]) : null, splits: splits.map(liveSeconds) } : null} run={solve.name ?? name} lastResult={fullTas.lastResult} canPlay={!busy && !!(run?.playable ?? run?.best)} onPlay={() => onCommand("/fulltas/play", { name })}
        picked={picked} onPick={(goal, shift) => { if (complete) pick(goal, goal, shift); }}/>
      {solve.outlook != null && <OutlookCard outlook={solve.outlook as Outlook} pulls={solve.running ? (solve.pulls ?? []) as PullNow[] : []}/>}
    </div>
  </div>;
}
