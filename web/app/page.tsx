"use client";

import { Fragment, useCallback, useEffect, useMemo, useRef, useState, type ReactElement } from "react";
import "./action-editor.css";
import "./frame-timeline.css";
import "./route-sidebar.css";
import "./fulltas.css";
import { FullTasSolvePanel, SegmentGroup, SegmentLegend, place, resetSegments, runTime, type FullTasOutcome, type FasterWay, type FullTasSolveState, type FullTasSplit } from "./fulltas-solve";
import { HumanSlackTimeline, type SlackReply } from "./human-slack";
import { TechPanel, type TechState } from "./techs";
import { BOX_KINDS, boxCost, boxTitle, UltraMap, type BoxCategory, type BoxInfo, type BoxMarker, type UltraData } from "./ultras";
import { artTiles, capturedStates, GROWN_COLOR, inState, MARKERS, SPRING_COLOR, StatePicker, TONE_COLORS, type ArtIndex, type StatePick, type TreeStep, type WorldState } from "./ultra-level";

const API = "http://127.0.0.1:17426/api";

type Action = { id: string; label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number; enabled: boolean; rawMoveX?: number; rawJumpPressed?: boolean; rawJumpHeld?: boolean; rawJumpReleased?: boolean; rawDashPressed?: boolean; rawDashHeld?: boolean };
type Route = { id: string; name: string; actionCount: number; actions: Action[] };
type RecordedAction = { label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number };
type Recording = { id: string; name: string; createdUtc: string; sampleCount: number; actionCount: number; courseTimeSeconds: number; actions: RecordedAction[] };
type Savestate = { id: string; name: string; createdUtc: string; assigned: boolean };
// A category's unlocks (Categories.cs): the label lists them in a fixed order (WJ, DS/DSn, DJ/DJn, BS, ORB, ZIP, OM;
// NONE when empty), and every unlock not in the label is off.
type Caps = { wallJump: boolean; dashes: number; airJumps: number; blockSwap: boolean; orbs: boolean; zip: boolean; omni: boolean };
type Category = { id: string; label: string; caps: Caps };
type Course = { id: number; token: string; bonus: boolean; categories: string[]; from?: string; to?: string; hint?: string | null; optional?: OptionalBlock[] };
type OptionalBlock = { name: string; note: string; on?: boolean; capabilities: string[] };
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; optional?: string[]; evaluations: number; finishes: number; turboChecks: number; turboSaved: number; toValidate?: number; best: number | null; log: string[]; courses: number[]; minutes?: number; repeat?: boolean; pass?: number; queue?: { label: string; result?: string | null; active: boolean }[] };
type FullTasRun = { name: string; start?: string | null; savestate?: string | null; goals: string[]; tokens?: string[]; breaker?: number | null; best: { routeId: string; name: string; time: number; splits: FullTasSplit[] } | null;
  offline?: { reached: number; time: number; splits: FullTasSplit[]; segments?: number[][]; faster?: FasterWay[] } | null; closest?: { reached: number; next: string | null; distance: number | null; ticks: number } | null; playable?: boolean; traceReady: boolean };
type FullTasState = { runs: FullTasRun[]; solve: FullTasSolveState; lastResult?: FullTasOutcome | null; goalsOverlay?: string };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; humanPlayback?: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean; overlayShown: boolean; breakerLightsForced?: boolean; checkpointRespawns?: boolean; fastPlayback?: boolean;
  saveUnlocks?: Caps | null;
  selectedRouteId: string; routes: Route[]; categories: Category[]; courses?: Course[]; recordings: Recording[]; savestates: Savestate[]; solver: SolverState;
  fullTas?: FullTasState;
  techs?: TechState;
};
// GET /state/static: the heavy part, fetched again only when /state/live's staticVersion changes.
type StaticState = { staticVersion: number; routes: Route[]; categories: Category[]; courses?: Course[]; recordings: Recording[]; savestates: Savestate[];
  fullTas?: Pick<FullTasState, "runs">; techs?: Pick<TechState, "list" | "detectors" | "labs"> };
// GET /state/live: everything else, polled.
type LiveState = Omit<StudioState, "routes" | "categories" | "courses" | "recordings" | "savestates" | "fullTas" | "techs"> & { staticVersion: number;
  fullTas?: Omit<FullTasState, "runs">; techs?: Pick<TechState, "injectedLab" | "seen"> };

const emptyState: StudioState = { connected: false, version: "", engineStatus: "Connecting…", running: false, recording: false, paused: false, capabilitiesLocked: false, overlayShown: true, selectedRouteId: "", routes: [], categories: [], recordings: [], savestates: [], solver: { running: false, status: "Idle", course: 0, capabilities: "", evaluations: 0, finishes: 0, turboChecks: 0, turboSaved: 0, best: null, log: [], courses: [1, 2, 3] } };

// The next live poll goes out this long after the last one's answer.
const LIVE_POLL_MS = 250;

function mergeState(s: StaticState, l: LiveState): StudioState {
  return { ...l, routes: s.routes, categories: s.categories, courses: s.courses, recordings: s.recordings, savestates: s.savestates,
    fullTas: s.fullTas && l.fullTas ? { ...s.fullTas, ...l.fullTas } : undefined,
    techs: s.techs && l.techs ? { ...s.techs, ...l.techs } : undefined };
}

async function request(path: string, body?: unknown) {
  const response = await fetch(`${API}${path}`, { method: body === undefined ? "GET" : "POST", headers: { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (!response.ok || data.error) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}

// Module-level so the Ultra map's load effect doesn't rerun on every render.
const loadUltras = (refresh: boolean): Promise<UltraData> => request("/enginesim/ultras", { refresh });
const loadSlack = (name: string): Promise<SlackReply> => request("/enginesim/slack", { name });

export default function Studio() {
  const [state, setState] = useState<StudioState>(emptyState);
  const [tab, setTab] = useState("fulltas");
  const [error, setError] = useState("");
  const [mapTarget, setMapTarget] = useState<MapTarget | null>(null);
  const saveUnlocks = state.connected ? state.saveUnlocks ?? null : null;

  // The static part and the refresh that fetched it; a refresh's answer is shown only if no later one was shown already.
  const staticState = useRef<{ from: number; data: StaticState } | null>(null);
  const refreshes = useRef(0), shown = useRef(0);
  const refresh = useCallback(async () => {
    const id = ++refreshes.current;
    try {
      const live: LiveState = await request("/state/live");
      let data = staticState.current?.data;
      if (!data || data.staticVersion !== live.staticVersion) {
        const fetched: StaticState = await request("/state/static");
        const known = staticState.current;
        if (known && id < known.from) data = known.data;
        else { staticState.current = { from: id, data: fetched }; data = fetched; }
      }
      if (id < shown.current) return;
      shown.current = id;
      setState(mergeState(data, live)); setError("");
    }
    catch {
      // A restarted game counts its versions from the start again.
      staticState.current = null;
      if (id < shown.current) return;
      shown.current = id;
      setState(s => ({ ...s, connected: false, engineStatus: "Game plugin offline" }));
    }
  }, []);

  // Polls one at a time: the next goes out LIVE_POLL_MS after the last one's answer.
  useEffect(() => {
    let alive = true, timer = 0;
    const poll = async () => { await refresh(); if (alive) timer = window.setTimeout(poll, LIVE_POLL_MS); };
    poll();
    return () => { alive = false; window.clearTimeout(timer); };
  }, [refresh]);
  const route = useMemo(() => state.routes.find(r => r.id === state.selectedRouteId) ?? state.routes[0], [state]);
  const command = async (path: string, body: unknown = {}) => {
    try { setError(""); await request(path, body); await refresh(); return true; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return false; }
  };
  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brandMark">T</span><div><strong>IGTAP</strong><small>TAS STUDIO</small></div></div>
      <div className={`connection ${state.connected ? "online" : "offline"}`}><i />{state.connected ? `Connected · v${state.version}` : "Waiting for game"}</div>
      <RouteSidebar routes={state.routes} courses={state.courses ?? []} categories={state.categories} activeId={route?.id} saveUnlocks={saveUnlocks} onCommand={command} />
      <div className="sidebarFooter"><p>Game keeps running while this window is open.</p><kbd>F8</kbd><span>Emergency stop</span></div>
    </aside>

    <section className="workspace">
      <div className="toggleBar">
        <label className={`capabilityLock ${state.capabilitiesLocked ? "on" : ""}`} title={state.capabilitiesLocked ? "On: your unlocks never change. Course solving needs this off." : "Off: routes and solving change unlocks to match; the game saves."}>
          <input type="checkbox" checked={state.capabilitiesLocked} disabled={!state.connected} onChange={e => command("/settings/lock-capabilities", { locked: e.target.checked })} />
          <strong>Don&apos;t touch my unlocks</strong>
        </label>
        <label className={`capabilityLock executable ${state.overlayShown ? "on" : ""}`} title="In game: shows hurtbox, spikes, velocity, recent inputs and jumps left.">
          <input type="checkbox" checked={state.overlayShown} disabled={!state.connected} onChange={e => command("/settings/overlay", { shown: e.target.checked })} />
          <strong>Overlay</strong>
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
        <div><p className="eyebrow">SELECTED ROUTE</p><h1>{route?.name ?? "No route"}</h1><p className="statusLine"><span className={state.running ? "pulse" : "dot"} />{state.humanPlayback && <span className="humanBadge" title="Human offline best: same time, fewer perfect inputs. Never saved.">HUMANIZED</span>}{state.engineStatus}</p></div>
        <div className="transport">
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/run")}>▶ Run route</button>
          <label className={`fastPlayback ${state.fastPlayback ? "on" : ""}`} title="On: 1x playback isn't held to 280 frames a second and runs as fast as the game renders. Game time per frame stays fixed, so it plays the same. Walks and probes stay paced.">
            <input type="checkbox" checked={!!state.fastPlayback} disabled={!state.connected} onChange={e => command("/settings/fast-playback", { on: e.target.checked })} />Fast
          </label>
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/walk", { slowdown: 10 })} title={"Replays the route 10× slower, human version when it's as fast."}>⏵ Walk route</button>
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/duplicate")}>Duplicate</button>
          <button className="secondary" disabled={!route} onClick={() => { const name=window.prompt("Rename route",route?.name); if(name?.trim()) command("/routes/rename",{routeId:route?.id,name:name.trim()}); }}>Rename</button>
          <button className="record" disabled={!route || state.running} onClick={() => command(state.recording ? "/record/stop" : "/record/start")}>{state.recording ? "■ Stop recording" : "● Record"}</button>
          <button className="danger" disabled={!state.running && !state.recording} onClick={() => command("/routes/stop")}>Stop</button>
        </div>
      </header>

      <nav className="tabs">{[["fulltas","Full-TAS"],["tech","Tech"],["ultras","Ultra map"],["optimize","Optimize"],["recordings","Recordings"],["savestates","Savestates"],["timeline","Timeline"]].map(([id,label]) => <button key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)}>{label}</button>)}</nav>
      {error && <div className="errorBanner">{error}<button onClick={() => setError("")}>×</button></div>}
      {state.paused && <div className="pauseBanner"><div><strong>The game is paused</strong><span>Automation needs the physics simulation running.</span></div><button onClick={() => command("/game/resume")}>Resume simulation</button></div>}

      <div className="content">
        {tab === "timeline" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">FIXED-TICK SEQUENCE</p><h2>Route timeline</h2></div><span className="hint">Changes save immediately</span></div><FrameTimeline actions={route?.actions ?? []}/><HumanSlackTimeline routeName={route?.name} load={loadSlack}/><div className="timeline">{route?.actions.length ? route.actions.map((action, index) => <ActionCard key={action.id} action={action} index={index} onSave={(updates) => command("/actions/update", { actionIndex: index, ...updates })} />) : <div className="emptyState"><span className="orbit">●</span><h2>No inputs yet</h2><p>Record a run to fill this route.</p></div>}</div></div>}

        {tab === "recordings" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">RAW FIXED-FRAME CAPTURES</p><h2>Recordings</h2></div><span className="hint">{state.recording ? "Recording now…" : `${state.recordings.length} saved`}</span></div>{state.recording&&<p className="recordingActive">● Recording is active. Finishing the course saves it automatically.</p>}<div className="recordingList">{state.recordings.map(recording=><div className="recordingCard" key={recording.id}><div className="recordingTitle"><div><strong>{recording.name}</strong><small>{new Date(recording.createdUtc).toLocaleString()} · {recording.sampleCount} physics frames · {recording.actionCount} actions</small></div>{recording.courseTimeSeconds>0&&<b>{recording.courseTimeSeconds.toFixed(3)}s</b>}</div><p>{recording.actions.map(action=>`${action.type.toLowerCase()} ${DirectionText(action.direction)} ${action.durationTicks}f${action.type==="WALLJUMP"&&action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?` (hold ${DirectionText(action.wallJumpHoldDirection)})`:""}`).join(" → ")}</p><div className="buttonRow"><button className="primary" onClick={()=>command("/recordings/create-route",{recordingId:recording.id})}>Create route</button><button title="Name it Course1_To_Course2 or Course2_To_Bonus2: a transition route." onClick={()=>command("/recordings/create-transition",{recordingId:recording.id})}>Create transition</button><button onClick={()=>{const name=window.prompt("Rename recording",recording.name);if(name?.trim())command("/recordings/rename",{recordingId:recording.id,name:name.trim()});}}>Rename</button><button className="danger" onClick={()=>{if(window.confirm(`Delete recording “${recording.name}”?`)) command("/recordings/delete",{recordingId:recording.id});}}>Delete</button></div></div>)}</div>{!state.recordings.length&&!state.recording&&<div className="emptyState"><span className="orbit">●</span><h2>No recordings yet</h2><p>Press Record, play the route, and finish the course.</p></div>}</div>}

        {tab === "fulltas" && <><FullTas fullTas={state.fullTas} running={state.running} onCommand={command} onMap={name => setMapTarget({ course: 0, capabilities: "", optional: [], fulltas: name })}/>{mapTarget?.fulltas && <SolveMap key={`fulltas_${mapTarget.fulltas}`} target={mapTarget} courses={state.courses ?? []} runs={(state.fullTas?.runs ?? []).map(r => r.name)} busy={state.running || !!state.fullTas?.solve.running} onClose={() => setMapTarget(null)}/>}</>}

        {tab === "tech" && <TechPanel techs={state.techs} running={state.running || state.recording} selectedRoute={route} onCommand={command} onEditInputs={async routeId => { if (await command("/routes/select", { routeId })) setTab("timeline"); }}/>}

        {tab === "ultras" && <UltraMap load={loadUltras}/>}

        {tab === "optimize" && <><div className="optimizeGrid"><Solver solver={state.solver ?? emptyState.solver} courses={state.courses ?? []} categories={state.categories} routes={state.routes} running={state.running} onCommand={command} onMap={setMapTarget}/><SolveMatrix routes={state.routes} courses={state.courses ?? []} categories={state.categories} solver={state.solver ?? emptyState.solver} onCommand={command}/></div>{mapTarget && <SolveMap key={`${mapTarget.course}_${mapTarget.capabilities}_${mapTarget.optional.join("+")}`} target={mapTarget} courses={state.courses ?? []} onClose={() => setMapTarget(null)}/>}</>}

        {tab === "savestates" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">START STATES</p><h2>Savestates</h2></div><button className="primary" onClick={() => command("/savestates/capture")}>Capture current state</button></div><p className="lede">Capture a game state, then assign it as a route start.</p><div className="savestateList">{!state.savestates.length && <div className="emptyState"><span className="orbit">●</span><h2>No savestates yet</h2><p>Capture one to start routes mid-game.</p></div>}{state.savestates.map(cp => <div className={`savestate ${cp.assigned ? "assigned" : ""}`} key={cp.id}><div><strong>{cp.name}</strong><small>{new Date(cp.createdUtc).toLocaleString()}</small></div>{cp.assigned && <span className="badge">Route start</span>}<button onClick={() => command("/savestates/restore", { savestateId: cp.id })}>Restore</button><button className="secondary" onClick={() => command("/savestates/assign", { savestateId: cp.assigned ? "" : cp.id })}>{cp.assigned ? "Unassign" : "Use as start"}</button></div>)}</div></div>}
      </div>
    </section>
  </main>;
}

// A category route's name: <course>_<label> [farm ]<time>. The label must name one of the registry's categories
// (state.categories), checked below; names like "4_almost_completed" go to the other routes.
const CATEGORY_ROUTE = /^([^_\s]+)_([A-Z][A-Z0-9_]*) (farm )?([\d.]+)$/i;

// Every unlock of a is in b (counts no higher): a narrower unlock set.
function capsSubset(a: Caps, b: Caps) {
  return (!a.wallJump || b.wallJump) && a.dashes <= b.dashes && a.airJumps <= b.airJumps
    && (!a.blockSwap || b.blockSwap) && (!a.orbs || b.orbs) && (!a.zip || b.zip) && (!a.omni || b.omni);
}

// The label's tokens joined ("WJ + DS2 + BS + ZIP + OM"), "None" when the set is empty.
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

// The unlocks spelled out: "Wall jump, dash (x1), block swap".
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
  bonus8: "Far above Course 4 (Course 6's box). Starts at Course 6.",
  bonus9: "Below and left of Course 9's start. Starts at Course 6.",
  bonus10: "Left of Course 7's start, lower. Starts at Course 6.",
  bonus11: "Just below Course 7's start, right. Starts at Course 6.",
  bonus12: "Above Course 7, below Course 9. Starts at Course 6.",
  bonus13: "Below Course 10, slightly left. Starts at Course 10's restart.",
  bonus14: "Right of Course 10. Starts at Course 10's restart.",
  bonus15: "Far left of Course 5, high up. Starts at Course 5's restart.",
  bonus16: "Above Course 1 (buyfree4b), level with Bonus 6. Starts at Course 4.",
  bonus17: "Far below Course 7 (jiggle drops). Starts at Course 7.",
  bonus18: "Left of Course 8's start (clone dust). Starts at Course 8.",
  bonus19: "The Tree in Area 1 (plants it). Starts at Course 10.",
  bonus20: "Vman start, overgrown Area 1, above Course 10. Starts at Course 10.",
  bonus21: "The other Vman start, just above Bonus 20. Starts at Course 10.",
  bonus22: "Above Course 5's start (secret #2, buyfree5). Starts at Course 5.",
  bonus23: "Left of Course 4's upgrades (buyblockswap5, after breaker). Starts at Course 5.",
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
  const best: Bests = new Map(), farm: Bests = new Map();
  const other: Route[] = [];
  for (const r of routes) {
    const m = CATEGORY_ROUTE.exec(r.name);
    const course = m && courses.find(c => c.token.toLowerCase() === m[1].toLowerCase());
    const cat = m ? categories.find(c => c.label.toLowerCase() === m[2].toLowerCase()) : undefined;
    if (!m || !course || !cat || !course.categories.includes(cat.id)) { other.push(r); continue; }
    const kind = m[3] ? farm : best, key = `${course.id}:${cat.id}`, time = Number(m[4]);
    const current = kind.get(key);
    if (!current || time < current.time) kind.set(key, {route: r, time});
    else other.push(r);
  }
  return {best, farm, other};
}

// The best route of this category or of a narrower one the course has (their unlocks are a subset).
function soloBest(best: Bests, c: Course, cat: Category, categories: Category[]) {
  let solo: {route: Route; time: number} | undefined;
  for (const narrower of categories.filter(n => c.categories.includes(n.id) && capsSubset(n.caps, cat.caps))) {
    const b = best.get(`${c.id}:${narrower.id}`);
    if (b && (!solo || b.time < solo.time)) solo = b;
  }
  return solo;
}

function RouteSidebar({routes,courses,categories,activeId,saveUnlocks,onCommand}:{routes:Route[];courses:Course[];categories:Category[];activeId?:string;saveUnlocks:Caps|null;onCommand:(path:string,body?:unknown)=>Promise<boolean>}) {
  const {best, farm, other} = categoryBests(routes, courses, categories);
  const cell = (routeId: string | undefined, text: string, title: string) => routeId
    ? <button className={`cmpCell ${routeId === activeId ? "active" : ""}`} title={title} onClick={() => onCommand("/routes/select", { routeId })}>{text}</button>
    : <span className="cmpCell empty" title={title}>{text}</span>;
  const rows = (c: Course) => categories.filter(cat => c.categories.includes(cat.id)).flatMap(cat => {
    const farms = c.bonus || c.to ? best : farm;
    if (!best.has(`${c.id}:${cat.id}`) && !farms.has(`${c.id}:${cat.id}`)) return [];
    const record = soloBest(best, c, cat, categories), lap = soloBest(farms, c, cat, categories);
    const beyond = !!saveUnlocks && !capsSubset(cat.caps, saveUnlocks);
    return [<div className={`cmpRow ${beyond ? "beyondUnlocks" : ""}`} key={cat.id} title={beyond ? `Your unlocks only reach ${shortCaps(saveUnlocks)}` : undefined}>
      <span>{shortCaps(cat.caps)}</span>
      {cell(record?.route.id, record ? record.time.toFixed(2) : "—", record?.route.name ?? "No record route")}
      {cell(lap?.route.id, lap ? lap.time.toFixed(2) : "—", lap ? `${lap.route.name}: ${Math.round(lap.time / .02)} ticks, quick restart to finish` : "No farm route yet")}
    </div>];
  });
  const section = (label: string, list: Course[]) => {
    const groups = list.map(c => ({c, r: rows(c)})).filter(g => g.r.length > 0);
    return groups.length > 0 && <>
      <div className="cmpHead"><label className="sectionLabel">{label}</label><span title="Record: the game's timer, start gate to end gate">Record</span><span title="Farm: ticks from quick restart to finish">Farm</span></div>
      {groups.map(({c, r}) => <div className="courseGroup" key={c.token}><strong className="courseName" title={placeTitle(c)}>{courseLabel(c, c.id)}</strong>{r}</div>)}
    </>;
  };
  return <div className="courseList">
    {section("COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id))}
    {section("TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id))}
    {section("BONUSES", courses.filter(c => c.bonus && !c.to).sort((a, b) => a.id - b.id))}
    {other.length > 0 && <details className="otherRoutes"><summary className="sectionLabel">OTHER ROUTES ({other.length})</summary>
      <div className="routeList">{other.map(item => <div className={`routeRow ${item.id === activeId ? "active" : ""}`} key={item.id} onContextMenu={(event) => { event.preventDefault(); if (window.confirm(`Delete route “${item.name}”?`)) onCommand("/routes/delete", { routeId: item.id }); }}><button className="routeItem" onClick={() => onCommand("/routes/select", { routeId: item.id })}><div><strong>{item.name}</strong><small>{item.actionCount} actions</small></div></button></div>)}</div>
    </details>}
  </div>;
}
function DirectionText(direction:number) { return direction < 0 ? "←" : direction > 0 ? "→" : "neutral"; }
function Field({label,children}:{label:string,children:React.ReactNode}) { return <label className="field"><span>{label}</span>{children}</label>; }
function FrameTimeline({actions}:{actions:Action[]}) {
  const frames=actions.flatMap((action,actionIndex)=>action.enabled?Array.from({length:Math.max(1,action.durationTicks)},(_,actionFrame)=>({action,actionIndex,actionFrame})):[]);
  return <section className="frameTimeline" aria-label="Frame-by-frame route inputs">
    <div className="frameTimelineHeader"><div><strong>Inputs by frame</strong><span>Scroll left → right · one column per physics frame</span></div><div className="frameLegend"><span>J↓ press</span><span>J hold</span><span>J↑ release</span><span>D dash</span></div></div>
    <div className="frameScroller"><div className="frameTrack">{frames.map(({action,actionIndex,actionFrame},frameIndex)=><FrameCell key={`${action.id}-${actionFrame}`} frame={frameIndex+1} action={action} actionIndex={actionIndex} actionFrame={actionFrame}/>)}</div></div>
  </section>;
}
function FrameCell({frame,action,actionIndex,actionFrame}:{frame:number;action:Action;actionIndex:number;actionFrame:number}) {
  const raw=action.type==="RAWINPUT";
  const move=raw?(action.rawMoveX??action.direction):action.type==="WALLJUMP"?(actionFrame===0?-action.direction:(action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?action.wallJumpHoldDirection:action.direction)):action.direction;
  const inputs:string[]=[];
  if(move<-.001) inputs.push(`←${Math.abs(move)<.999?Math.round(Math.abs(move)*100):""}`);
  else if(move>.001) inputs.push(`→${Math.abs(move)<.999?Math.round(Math.abs(move)*100):""}`);
  if((raw&&action.rawJumpPressed)||(!raw&&actionFrame===0&&(action.type==="JUMP"||action.type==="WALLJUMP"))) inputs.push("J↓");
  else if(raw&&action.rawJumpHeld) inputs.push("J");
  if(raw&&action.rawJumpReleased) inputs.push("J↑");
  if((raw&&action.rawDashPressed)||(!raw&&actionFrame===0&&action.type==="DASH")) inputs.push("D↓");
  else if(raw&&action.rawDashHeld) inputs.push("D");
  return <div className={`frameCell actionTone${actionIndex%6} ${actionFrame===0?"actionStart":""}`} title={`Frame ${frame} · action ${actionIndex+1}: ${action.label}`}><b>{frame}</b><span>{inputs.length?inputs.join(" "):"·"}</span><small>{actionFrame===0?`A${actionIndex+1}`:""}</small></div>;
}
function ActionCard({action,index,onSave}:{action:Action;index:number;onSave:(u:Record<string,unknown>)=>void}) { const [duration,setDuration]=useState(action.durationTicks); useEffect(()=>setDuration(action.durationTicks),[action.durationTicks]); const hold=action.type==="WALLJUMP"?` · post-launch ${action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?DirectionText(action.wallJumpHoldDirection):"auto"}`:""; return <div className={`actionCard ${!action.enabled ? "disabled" : ""}`}><b>{index+1}</b><div className="actionMain"><strong>{action.label}</strong><small>Physics frames{hold}</small></div><select value={action.type} onChange={e=>onSave({type:e.target.value})}>{["RAWINPUT","MOVE","JUMP","DASH","WAIT","WALLJUMP"].map(type=><option key={type} value={type}>{type==="RAWINPUT"?"EXACT INPUT":type}</option>)}</select><div className="direction">{action.direction < 0 ? "←" : action.direction > 0 ? "→" : "•"}</div><div className="inlineInput"><input type="number" min="1" value={duration} onChange={e=>setDuration(Number(e.target.value))}/><span>f</span></div><button onClick={()=>onSave({durationTicks:duration})}>Save</button><button className="secondary" onClick={()=>onSave({enabled:!action.enabled})}>{action.enabled ? "Disable" : "Enable"}</button></div>; }
function Solver({solver,courses,categories,routes,running,onCommand,onMap}:{solver:SolverState;courses:Course[];categories:Category[];routes:Route[];running:boolean;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(target:MapTarget)=>void}) {
  const [course,setCourse]=useState<number|"">("");
  const [caps,setCaps]=useState("wj");
  const chosen=course===""?(solver.courses[0]??1):course;
  const chosenCourse=courses.find(c=>c.id===chosen);
  const capsOptions=chosenCourse&&chosenCourse.categories.length?categories.filter(cat=>chosenCourse.categories.includes(cat.id)):categories;
  const chosenCat=capsOptions.find(cat=>cat.id===caps)??capsOptions[0];
  const chosenCaps=chosenCat?.id??"";
  // Names whose checkbox was flipped from its forced-paths.json default ("default": true starts it checked).
  const [optional,setOptional]=useState<string[]>([]);
  const optionalBlocks=(chosenCourse?.optional??[]).filter(b=>b.capabilities.includes(chosenCaps));
  const chosenOptional=optionalBlocks.filter(b=>!!b.on!==optional.includes(b.name)).map(b=>b.name).sort();
  const target={course:chosen,capabilities:chosenCaps,optional:chosenOptional};
  const solo=chosenCourse&&chosenCat?soloBest(categoryBests(routes,courses,categories).best,chosenCourse,chosenCat,categories):undefined;
  const runRoute=async()=>{ if(solo&&await onCommand("/routes/select",{routeId:solo.route.id})) await onCommand("/routes/run"); };
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">OFFLINE SOLVER</p><h2>Solve a course</h2></div><span className={`offlineStatus ${solver.running?"running":""}`}>{solver.running?"Solving":"Idle"}</span></div>
    <p className="lede">Captures the start in game, then searches offline on every core.</p><details className="howTo"><summary>How it works</summary><ol><li>Only the start and stop timer gates matter.</li><li>Every offline best is replayed in game (turbo check).</li><li>Held-up bests save as record; farm if fewer ticks.</li><li>Farm time = ticks × 0.02 s, restart to finish.</li></ol></details>
    <div className="offlineControls">
      <Field label="1. Course"><select value={chosen} disabled={solver.running} title={placeTitle(chosenCourse)} onChange={e=>{ if(e.target.value==="new") { let id=1; while(solver.courses.includes(id)) id++; onCommand("/courses/add-from-recording").then(()=>setCourse(id)); } else setCourse(Number(e.target.value)); }}>{solver.courses.map(id=>{ const c=courses.find(c=>c.id===id); return <option key={id} value={id} title={placeTitle(c)}>{courseLabel(c,id)}</option>; })}<option value="new">+ New course from last recording</option></select></Field>
      <Field label="2. Capabilities"><select value={chosenCaps} disabled={solver.running} onChange={e=>setCaps(e.target.value)}>{capsOptions.map(cat=><option key={cat.id} value={cat.id}>{describeCaps(cat.caps)}</option>)}</select></Field>
      {optionalBlocks.length>0&&<div className="field"><span>Optional avoid blocks</span>{optionalBlocks.map(b=><label key={b.name} className="passLoop" title={b.note}><input type="checkbox" disabled={solver.running} checked={chosenOptional.includes(b.name)} onChange={()=>setOptional(o=>o.includes(b.name)?o.filter(n=>n!==b.name):[...o,b.name])}/>{b.name}</label>)}</div>}
    </div>
    <div className="buttonRow">{solver.running?<button className="danger" onClick={()=>onCommand("/solver/stop")}>Stop</button>:<button className="primary" onClick={()=>onCommand("/solver/start",target)}>3. Solve</button>}<button className="secondary" onClick={()=>onMap(target)} title="Draws the explored area, saved routes, best and forced-path boxes.">Show map</button><button className="secondary" disabled={!solo||running||solver.running} onClick={runRoute} title={solo?`Run ${solo.route.name} in the game`:"No saved route for this course and category"}>▶ Run route</button><button className="secondary" disabled={running||solver.running} onClick={()=>onCommand("/enginesim/play",{course:chosen,capabilities:chosenCaps})} title={"Plays this category's newest offline best. Nothing is saved."}>▶ Play offline best</button><button className="secondary humanButton" disabled={running||solver.running} onClick={()=>onCommand("/enginesim/play",{course:chosen,capabilities:chosenCaps,human:true})} title={"Plays the humanized best for practice. Nothing is saved."}>▶ Play human version</button></div>
    <div className="offlineSummary"><div><span>Best</span><strong>{solver.best!=null?`${solver.best.toFixed(2)}s`:"—"}</strong><small>{solver.course?`${courseLabel(courses.find(c=>c.id===solver.course),solver.course)} · ${solver.capabilities}${solver.optional?.length?` +${solver.optional.join(" +")}`:""}`:"Not started"}</small></div><div><span>Runs</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.finishes.toLocaleString()} timed finishes</small></div><div><span>Turbo checks</span><strong>{solver.turboChecks.toLocaleString()}</strong><small>{solver.turboSaved.toLocaleString()} saved as routes</small></div><div><span>To validate</span><strong>{(solver.toValidate??0).toLocaleString()}</strong><small>offline bests waiting for a turbo check</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.log.length>0&&<ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul>}
  </div>;
}

type MapTarget = { course: number; capabilities: string; optional: string[]; fulltas?: string };
// `when`: the world state it's only in ("<state>:<option>"), none: every one. An "area" is ground off in the capture:
// its outline rings end to end in `p`, `rings` the length of each, filled even-odd.
type MapSolid = { k: "poly" | "line" | "circle" | "capsule" | "area"; spike: boolean; p: number[]; r: number; tone?: "blue" | "orange" | null; spring?: number[] | null; when?: string | null; rings?: number[] | null };
type MapRoute = { name: string; kind: "seed" | "best" | "plateau" | "human"; finished: boolean; time: number; timerStart: number; points: number[]; cells?: number[][]; state?: number[] };
// Ghost compare (`/api/enginesim/ghost`, served from the map JSON `map` wrote to disk): two routes' per-tick positions,
// per-cell first arrivals and per-tick state fingerprints, with the first tick their states diverge.
type GhostRoute = MapRoute & { cells: number[][]; state: number[] };
type GhostData = { cell: number; a: GhostRoute; b: GhostRoute; diverge: { tick: number; x: number; y: number } | null };
const GHOST_A = "#4fc3f7", GHOST_B = "#ffb74d";
// Every map coordinate is in the game world's frame (courses.json's, forced-paths.json's, --via/--avoid's, at:x,y goals'):
// the same for every capture of a place, whatever FloatingOrigin the game had then.
type MapData = { course: number; capabilities: string; label: string; fulltas?: string; bounds: number[]; solids: MapSolid[];
  // The trees' ground where it grows once its tree upgrades are bought (not in the world the run starts in).
  grown?: MapSolid[]; treeSteps?: TreeStep[];
  // `area`: a checkpoint's trigger (touching it sets the spawn) or a box's trigger, polygons of x, y pairs. `spawn`:
  // the player's box where a death or quick restart puts it at a checkpoint (x0, y0, x1, y1). `box`: kind "box" only,
  // a buy box's info.
  markers: { kind: string; name: string; x: number; y: number; area?: number[][]; spawn?: number[]; when?: string | null; box?: BoxInfo }[]; avoid: number[][]; via: number[][]; goals?: MapGoal[]; optional?: { name: string; note: string; on: boolean; avoid: number[][]; via: number[][] }[]; routes: MapRoute[];
  // The world states the level can be drawn in; `current`: the capture's, which the routes and explored cells are from.
  states?: WorldState[];
  explored: { cell: number; runs: number; prefixes: number; cells: number[][] } };
// A Full-TAS run's at: goal: its number in the run's goal list (1-based, as the goals table), its label and its rect (x0, y0, x1, y1).
type MapGoal = { index: number; label: string; box: number[] };
type MapLayers = { art: boolean; tree: boolean; shapes: boolean; explored: boolean; boxes: boolean; markers: boolean } & Record<`box:${BoxCategory}`, boolean>;
const ROUTE_COLORS = ["#4fc3f7", "#ff8a65", "#ba68c8", "#81c784", "#f06292", "#fff176", "#4db6ac", "#9575cd"];

function SolveMap({target,courses,runs,busy,onClose}:{target:MapTarget;courses:Course[];runs?:string[];busy?:boolean;onClose:()=>void}) {
  const [data, setData] = useState<MapData | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState("");
  const [layers, setLayers] = useState<MapLayers>({ art: true, tree: true, shapes: false, explored: true, boxes: true, markers: true,
    // The one-off buy box kinds on (what you're looking for), the dense repeatables off (one question each).
    "box:secret": true, "box:bonusUnique": true, "box:bonusBoost": true, "box:achievement": true,
    "box:movement": true, "box:unlock": true, "box:tree": false, "box:course": false });
  // The game's art (web/public/art, tools/render-art.py; none: the level is its collision shapes). Its tiles load as
  // they're drawn and redraw the map when they arrive.
  const [art, setArt] = useState<ArtIndex | null>(null);
  useEffect(() => { fetch("/art/index.json").then(r => r.ok ? r.json() as Promise<ArtIndex> : null).then(setArt, () => setArt(null)); }, []);
  const artImages = useRef(new Map<string, HTMLImageElement>());
  const [artLoads, setArtLoads] = useState(0);
  // The world state the level is drawn in (Area 1 normal or overgrown); the capture's until picked.
  const [worldState, setWorldState] = useState<StatePick>({});
  const markers = useMemo(() => (data?.markers ?? []).filter(m => inState(m.when, worldState)), [data, worldState]);
  const [hidden, setHidden] = useState<Set<number>>(new Set());
  const [cursor, setCursor] = useState<{ x: number; y: number; marker?: string; reached?: number; delta?: number } | null>(null);
  // The buy box tapped (not dragged over): its tooltip card where hover titles aren't a thing.
  const [boxTip, setBoxTip] = useState<BoxMarker | null>(null);
  // The last press panned the map, so its click is no tap on a box.
  const panned = useRef(false);
  const arrivals = useMemo(() => new Map(data?.explored.cells.map(([cx, cy, t]) => [`${cx},${cy}`, t] as [string, number]) ?? []), [data]);
  // Ghost compare: two routes picked from the map (saved, offline best, human version), drawn over it with their first
  // state-divergence tick and, per map cell along the paths, the time delta at first arrival (B − A).
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
  // Cells both routes reach, by how much later B first arrives (seconds), and cells only one of them reaches.
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
  // Full-TAS: an avoid box drawn on the map (world x0, y0, x1, y1 as dragged) and the runs it goes to.
  const [drawing, setDrawing] = useState(false);
  const [sketch, setSketch] = useState<number[] | null>(null);
  const [sketchRuns, setSketchRuns] = useState<string[]>(target.fulltas ? [target.fulltas] : []);
  // Course maps: the avoid box's optional block name (empty: cut for every solve) and whether that block starts on.
  const [sketchName, setSketchName] = useState("");
  const [sketchOn, setSketchOn] = useState(false);
  const [saving, setSaving] = useState(false);
  const sketchStart = useRef<{ x: number; y: number } | null>(null);
  // Full-TAS: an at: goal dragged to a new place (same size), shown there until it's saved or discarded.
  const [moving, setMoving] = useState(false);
  const [moved, setMoved] = useState<{ goal: MapGoal; box: number[] } | null>(null);
  const grab = useRef<{ goal: MapGoal; box: number[]; x: number; y: number } | null>(null);
  const colors = useMemo(() => data?.routes.map((r, i) => r.kind === "best" ? "#ffd54f" : r.kind === "human" ? "#c77dff" : r.kind === "plateau" ? "rgba(255,255,255,.55)" : ROUTE_COLORS[i % ROUTE_COLORS.length]) ?? [], [data]);
  const view = useRef({ ox: 0, oy: 0, scale: 1 });
  const drag = useRef<{ x: number; y: number } | null>(null);
  const fitted = useRef(false);

  // Only the latest load lands: an older one still in flight (a Redraw, a second mount) neither overwrites its data nor
  // leaves its error standing.
  const loads = useRef(0);
  const load = useCallback(async () => {
    const id = ++loads.current;
    setLoading(true); setFailure("");
    try {
      const d: MapData = target.fulltas ? await request("/fulltas/map", { name: target.fulltas }) : await request("/enginesim/map", target);
      if (id !== loads.current) return;
      setData(d); setFailure(""); setWorldState(w => ({ ...capturedStates(d.states), ...w })); setHidden(new Set(d.routes.map((r, i) => r.kind === "plateau" ? i : -1).filter(i => i >= 0)));
      if (!fitted.current) { fit(d); fitted.current = true; }
    }
    catch (e) { if (id === loads.current) setFailure(e instanceof Error ? e.message : String(e)); }
    finally { if (id === loads.current) setLoading(false); }
  }, [target]);
  useEffect(() => { load(); }, [load]);

  function fit(d: MapData) {
    const c = canvas.current; if (!c) return;
    const [x0, y0, x1, y1] = d.bounds, scale = Math.min(c.clientWidth / (x1 - x0), c.clientHeight / (y1 - y0));
    view.current = { scale, ox: (x0 + x1) / 2 - c.clientWidth / 2 / scale, oy: (y0 + y1) / 2 + c.clientHeight / 2 / scale };
  }

  const draw = useCallback(() => {
    const c = canvas.current; if (!c || !data) return;
    const dpr = window.devicePixelRatio || 1, w = c.clientWidth, h = c.clientHeight;
    if (c.width !== w * dpr || c.height !== h * dpr) { c.width = w * dpr; c.height = h * dpr; }
    const g = c.getContext("2d"); if (!g) return;
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
        // The canvas is y up; the image is drawn upright.
        g.save(); g.translate(t.x, t.y + t.size); g.scale(1, -1); g.drawImage(img, 0, 0, t.size, t.size); g.restore();
      }
    };
    if (artShown) drawArt(worldState);
    // The trees' own tile set ("tree:grown", tools/render-art.py) over the level: off in the fresh world, so it's drawn
    // under the tree layer.
    if (artShown && layers.tree) drawArt({ ...worldState, tree: "grown" });
    if (layers.explored && data.explored.cells.length) {
      const size = data.explored.cell;
      // Over the art, see-through.
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
        // Launch direction: from the box's middle out past its far side.
        const [ux, uy] = s.spring, n = s.p.length / 2;
        let cx = 0, cy = 0, reach = 0;
        for (let i = 0; i < n; i++) { cx += s.p[2 * i] / n; cy += s.p[2 * i + 1] / n; }
        for (let i = 0; i < n; i++) reach = Math.max(reach, (s.p[2 * i] - cx) * ux + (s.p[2 * i + 1] - cy) * uy);
        // Only once zoomed in (6 world units per pixel or closer, as the Ultra map); zoomed out a spring is its box.
        if (px > 6) continue;
        const len = reach + 40, head = 16, tx = cx + ux * len, ty = cy + uy * len;
        g.lineWidth = 2 * px; g.beginPath(); g.moveTo(cx, cy); g.lineTo(tx, ty);
        g.moveTo(tx - head * (ux + uy), ty - head * (uy - ux)); g.lineTo(tx, ty); g.lineTo(tx - head * (ux - uy), ty - head * (uy + ux)); g.stroke();
      }
    }
    // Tree ground, once grown: dashed outlines, lighter than the level the engine has.
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
    if (layers.boxes) for (const [boxes, color] of [[data.avoid, "#ff3030"], [data.via, "#3ddc97"]] as [number[][], string][])
      for (const [x0, y0, x1, y1] of boxes) {
        g.fillStyle = color; g.globalAlpha = .16; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1;
        g.strokeStyle = color; g.lineWidth = 2 * px; g.strokeRect(x0, y0, x1 - x0, y1 - y0);
      }
    if (layers.boxes) for (const block of data.optional ?? [])
      for (const [boxes, color] of [[block.avoid, "#ff3030"], [block.via, "#3ddc97"]] as [number[][], string][])
        for (const [x0, y0, x1, y1] of boxes) {
          if (block.on) { g.fillStyle = color; g.globalAlpha = .16; g.fillRect(x0, y0, x1 - x0, y1 - y0); g.globalAlpha = 1; }
          g.strokeStyle = color; g.lineWidth = 2 * px; g.setLineDash(block.on ? [] : [6 * px, 4 * px]); g.strokeRect(x0, y0, x1 - x0, y1 - y0); g.setLineDash([]);
        }
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
    // A ghost compare fades the overview and draws its two routes over it (A solid, B dashed).
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
      // Per map cell along the paths: how much later B first arrives than A (blue: B earlier, red: B later).
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
    if (layers.markers) for (const m of markers) {
      // A buy box is drawn in its category's colour and shape, over its trigger box where the category is on.
      const look = m.kind === "goal" ? { color: "#b9f6ca", shape: "square" } : m.kind === "box" ? BOX_KINDS[m.box!.category] : MARKERS[m.kind];
      if (m.kind === "box") {
        if (!layers[`box:${m.box!.category}`]) continue;
        if (m.area) {
          g.lineWidth = 1.5 * px; g.strokeStyle = look!.color; g.setLineDash([5 * px, 3 * px]);
          for (const poly of m.area) {
            if (poly.length < 6) continue;
            g.beginPath(); g.moveTo(poly[0], poly[1]);
            for (let i = 2; i + 1 < poly.length; i += 2) g.lineTo(poly[i], poly[i + 1]);
            g.closePath();
            g.globalAlpha = .1; g.fill();
            g.globalAlpha = 1; g.stroke();
          }
          g.setLineDash([]);
        }
      }
      const color = look?.color ?? "#4fc3f7";
      g.fillStyle = color;
      const s = (look?.shape === "small" ? 4 : look?.shape === "star" ? 10 : 6) * px;
      // A checkpoint's two boxes at world size: its trigger (touching it sets the spawn, dashed) and the player's box
      // where a death or quick restart respawns it (solid), as the Ultra map draws them.
      if (m.kind === "checkpoint") {
        g.lineWidth = 1.5 * px; g.strokeStyle = color;
        if (m.area) {
          g.setLineDash([5 * px, 3 * px]);
          for (const poly of m.area) {
            if (poly.length < 6) continue;
            g.beginPath(); g.moveTo(poly[0], poly[1]);
            for (let i = 2; i + 1 < poly.length; i += 2) g.lineTo(poly[i], poly[i + 1]);
            g.closePath();
            g.globalAlpha = .1; g.fill();
            g.globalAlpha = 1; g.stroke();
          }
          g.setLineDash([]);
        }
        if (m.spawn) g.strokeRect(m.spawn[0], m.spawn[1], m.spawn[2] - m.spawn[0], m.spawn[3] - m.spawn[1]);
      }
      if (look?.shape === "circle") { g.beginPath(); g.arc(m.x, m.y, s, 0, Math.PI * 2); g.fill(); }
      else if (look?.shape === "diamond") { g.beginPath(); g.moveTo(m.x, m.y + s * 1.3); g.lineTo(m.x + s, m.y); g.lineTo(m.x, m.y - s * 1.3); g.lineTo(m.x - s, m.y); g.closePath(); g.fill(); }
      else if (look?.shape === "star") {
        g.beginPath();
        for (let i = 0; i < 10; i++) { const a = Math.PI / 2 + i * Math.PI / 5, k = i % 2 ? s * .45 : s; g.lineTo(m.x + k * Math.cos(a), m.y + k * Math.sin(a)); }
        g.closePath(); g.fill(); g.strokeStyle = "#07111f"; g.lineWidth = px; g.stroke();
      }
      else g.fillRect(m.x - s, m.y - s, 2 * s, 2 * s);
    }
  }, [data, layers, worldState, markers, art, artLoads, hidden, colors, latest, sketch, moved, ghost, ghostCells]);
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
  const onDown = (e: React.MouseEvent<HTMLCanvasElement>) => {
    panned.current = false;
    if (moving) {
      // The goal under the pointer: the smallest rect holding it (a goal being moved is where it was dragged to).
      const p = world(e), hit = (data?.goals ?? [])
        .map(goal => ({ goal, box: moved?.goal.index === goal.index ? moved.box : goal.box }))
        .filter(({ box }) => p.x >= box[0] && p.x <= box[2] && p.y >= box[1] && p.y <= box[3])
        .sort((a, b) => (a.box[2] - a.box[0]) * (a.box[3] - a.box[1]) - (b.box[2] - b.box[0]) * (b.box[3] - b.box[1]))[0];
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
    if (!sketchStart.current) return;
    sketchStart.current = null;
    setDrawing(false);
    setSketch(s => s && Math.abs(s[2] - s[0]) > 1 && Math.abs(s[3] - s[1]) > 1 ? s : null);
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
    setCursor({ x, y, marker: near ? (near.box ? boxTitle(near) : `${near.name} (${near.kind})`) : undefined, reached: arrivals.get(cell), delta: ghostCells?.deltaAt.get(cell) });
  };
  // A tap (not a pan) on a buy box pins its card: what it is, gives and costs.
  const onClick = (e: React.MouseEvent<HTMLCanvasElement>) => {
    if (panned.current) return;
    const p = world(e), reach = 10 / view.current.scale;
    let near: BoxMarker | null = null, best = reach;
    for (const m of markers as BoxMarker[]) {
      if (m.kind !== "box" || !layers[`box:${m.box!.category}`]) continue;
      const gap = Math.hypot(m.x - p.x, m.y - p.y);
      if (gap < best) { best = gap; near = m; }
    }
    if (near) { setBoxTip(near); setCursor({ x: p.x, y: p.y, marker: boxTitle(near) }); }
  };
  const editAvoid = async (box: number[], names: string[], remove: boolean) => {
    setSaving(true); setFailure("");
    try { await request("/fulltas/avoid", { box, runs: names, remove }); if (!remove) setSketch(null); await load(); }
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
    try { const to = center(moved.box); await request("/fulltas/move-goal", { name: data.fulltas, index: moved.goal.index - 1, label: moved.goal.label, x: to.x, y: to.y }); setMoved(null); await load(); }
    catch (e) { setFailure(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  };
  const boxText = (b: number[]) => `${Math.min(b[0], b[2]).toFixed(1)}, ${Math.min(b[1], b[3]).toFixed(1)} to ${Math.max(b[0], b[2]).toFixed(1)}, ${Math.max(b[1], b[3]).toFixed(1)}`;
  const toggle = (key: keyof MapLayers) => setLayers(l => ({ ...l, [key]: !l[key] }));
  return <div className="panel offlinePanel solveMap"><div className="panelTitle"><div><p className="eyebrow">SEARCH MAP</p><h2>{target.fulltas ? `Full-TAS · ${target.fulltas}` : <>{courseLabel(courses.find(c => c.id === target.course), target.course)} · {target.capabilities}{target.optional.map(n => ` +${n}`).join("")}</>}</h2></div>
    <div className="buttonRow"><button className="secondary" disabled={loading} onClick={load}>{loading ? "Drawing…" : "Redraw"}</button><button className="secondary" disabled={!data} onClick={() => { if (data) { fit(data); draw(); } }}>Fit</button><button className="secondary" onClick={onClose}>Close</button></div></div>
    {target.fulltas
      ? <><p className="lede">Shows this run&apos;s search windows, best route, goals and avoid boxes.</p><details className="howTo"><summary>How it works</summary><ul><li>Color: when a window first arrived; blue early, red late.</li><li>Gold: best so far, replayed from the run&apos;s start.</li><li>Green boxes: <code>at:</code> goals; red: avoid boxes.</li><li>Avoid boxes come from forced-paths.json rules naming this run.</li><li>Draw avoid drags a box; Move goals drags goal boxes.</li><li>Wheel zooms, drag pans; coordinates match forced-paths.json and <code>at:</code>.</li></ul></details></>
      : <><p className="lede">The solver&apos;s level, explored area, saved routes and forced-path boxes.</p><details className="howTo"><summary>How it works</summary><ul><li>Color: fastest time reaching each spot; blue early, red late.</li><li>Grey: reached only with the timer off.</li><li>Gold: fastest offline best. Ring: a route&apos;s timer start.</li><li>Red boxes cut runs; dashed boxes are optional and off.</li><li>Draw avoid drags a cut box; name it for a block.</li><li>Ghost compare shows two routes&apos; divergence and arrival deltas.</li></ul></details></>}
    {failure && <p className="mapFailure">{failure}</p>}
    <div className="mapLayers">
      {([["art", "Game art"], ["tree", "Tree (grown)"], ["shapes", "Collision shapes"], ["explored", "Explored"], ["boxes", "Forced-path boxes"], ["markers", "Gates, checkpoints, teleporters, refills"],
        ...Object.entries(BOX_KINDS).map(([k, m]) => [`box:${k}`, `Buy box: ${m.text}`] as [keyof MapLayers, string])] as [keyof MapLayers, string][]).filter(([key]) => art || (key !== "art" && key !== "tree" && key !== "shapes")).map(([key, label]) =>
        <label key={key} className="passLoop"><input type="checkbox" checked={layers[key]} onChange={() => toggle(key)}/>{label}</label>)}
      {data?.states && <StatePicker states={data.states} pick={worldState} onPick={setWorldState} className="passLoop"/>}
      {data && <span className="hint">{data.fulltas ? `${data.explored.cells.length.toLocaleString()} explored cells` : `${data.explored.prefixes.toLocaleString()} explore prefixes · ${data.explored.runs.toLocaleString()} runs`}</span>}
      {data && <span className="hint">{data.fulltas ? "run time" : "timer"} at first arrival: <span style={{ display: "inline-block", width: 90, height: 8, verticalAlign: "middle", background: "linear-gradient(90deg,hsl(230,90%,55%),hsl(172,90%,55%),hsl(115,90%,55%),hsl(57,90%,55%),hsl(0,90%,55%))" }}/> 0–{latest.toFixed(1)}s{!data.fulltas && <> · <span style={{ display: "inline-block", width: 8, height: 8, background: "#34383f", verticalAlign: "middle" }}/> timer off</>}</span>}
      {data && <button className={drawing ? "primary" : "secondary"} disabled={saving} title="Drag a box: search runs entering it are cut." onClick={() => { setDrawing(d => !d); setSketch(null); setMoving(false); }}>{drawing ? "Drag a box…" : "Draw avoid"}</button>}
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
    <canvas ref={canvas} className="mapCanvas" style={drawing ? { cursor: "crosshair" } : moving ? { cursor: "move" } : undefined} onMouseDown={onDown} onMouseUp={onUp} onMouseLeave={() => { onUp(); setCursor(null); }} onMouseMove={onMove} onClick={onClick}/>
    {boxTip?.box && <div className="mapReadout"><strong>{boxTip.name}</strong> · {BOX_KINDS[boxTip.box.category].text} · gives {boxTip.box.gives} · {boxTip.box.upgrade} · {boxCost(boxTip.box)} · {boxTip.box.cap === 1 ? "one-off" : `repeatable ×${boxTip.box.cap}`} · {boxTip.box.secret ? `secret${boxTip.box.number ? ` #${boxTip.box.number}` : ""} · ` : ""}{boxTip.box.ach ? `${boxTip.box.ach} · ` : ""}{boxTip.box.tree ? "tree upgrade · " : ""}{boxTip.box.fresh ? "in the fresh world" : "only later"}
      <button className="secondary" onClick={() => setBoxTip(null)}>×</button></div>}
    <div className="mapReadout">{cursor ? <>world {cursor.x.toFixed(1)}, {cursor.y.toFixed(1)}{cursor.reached != null && <> · {cursor.reached < 0 ? "reached only with the timer off" : `first reach ${cursor.reached.toFixed(2)}s`}</>}{cursor.delta != null && <> · Δ {cursor.delta > 0 ? "+" : ""}{cursor.delta.toFixed(2)}s (B − A)</>}{cursor.marker && <> · <strong>{cursor.marker}</strong></>}</> : "Point at the map for coordinates."}</div>
    {sketch && !sketchStart.current && <div className="buttonRow">
      <span className="hint">Avoid {boxText(sketch)} for</span>
      {data?.fulltas ? <>
        {(runs?.length ? runs : sketchRuns).map(n => <label key={n} className="passLoop"><input type="checkbox" checked={sketchRuns.includes(n)} onChange={() => setSketchRuns(r => r.includes(n) ? r.filter(k => k !== n) : [...r, n])}/>{n}</label>)}
        <button className="primary" disabled={saving || !sketchRuns.length} onClick={() => editAvoid(sketch, sketchRuns, false)}>Add avoid</button>
      </> : <>
        <input placeholder="block name (optional)" value={sketchName} onChange={e => setSketchName(e.target.value)} title="Empty: cut for every solve. Named: a pickable optional block."/>
        <label className="passLoop" title="Starts on for solves that pick no blocks."><input type="checkbox" disabled={!sketchName.trim()} checked={sketchOn} onChange={e => setSketchOn(e.target.checked)}/>on by default</label>
        <button className="primary" disabled={saving} onClick={() => editCourseAvoid(sketch, sketchName.trim(), sketchOn, false)}>Add avoid</button>
      </>}
      <button className="secondary" disabled={saving} onClick={() => setSketch(null)}>Discard</button></div>}
    {moved && <div className="buttonRow">
      <span className="hint">Goal {moved.goal.index} <code>{moved.goal.label}</code>: centre {center(moved.goal.box).x}, {center(moved.goal.box).y} to</span>
      <Field label="x"><input type="number" step="0.1" value={center(moved.box).x} onChange={e => placeMoved(Number(e.target.value), center(moved.box).y)}/></Field>
      <Field label="y"><input type="number" step="0.1" value={center(moved.box).y} onChange={e => placeMoved(center(moved.box).x, Number(e.target.value))}/></Field>
      <button className="primary" disabled={saving || busy} title={busy ? "Stop solving or playing first." : "Saves the goal in fulltas.json."} onClick={saveMoved}>Save goal</button>
      <button className="secondary" disabled={saving} onClick={() => setMoved(null)}>Discard</button></div>}
    {data?.fulltas && data.avoid.length > 0 && <ul className="mapRoutes">{data.avoid.map((b, i) => <li key={i}><span className="mapSwatch" style={{ background: "#ff3030" }}/>avoid {boxText(b)}
      <button className="secondary" disabled={saving} title="Removes this box; shared boxes go for other runs too." onClick={() => editAvoid(b, [data.fulltas!], true)}>Remove</button></li>)}</ul>}
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

type SolveTarget = { key: string; body: { course: number; capabilities: string }; time?: number; farm?: number };

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

function SolveMatrix({routes,courses,categories,solver,onCommand}:{routes:Route[];courses:Course[];categories:Category[];solver:SolverState;onCommand:(path:string,body?:unknown)=>Promise<boolean>}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [passes, updatePasses] = usePasses();
  useEffect(() => setSelected(new Set(JSON.parse(window.localStorage.getItem("optimizeSelection") ?? "[]"))), []);
  const select = (next: Set<string>) => { setSelected(next); window.localStorage.setItem("optimizeSelection", JSON.stringify([...next])); };
  const toggle = (keys: string[]) => {
    const next = new Set(selected), on = keys.some(k => !next.has(k));
    for (const k of keys) { if (on) next.add(k); else next.delete(k); }
    select(next);
  };
  const {best, farm} = categoryBests(routes, courses, categories);
  const groups: [string, Course[]][] = ([
    ["COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id)],
    ["TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id)],
    ["BONUSES", courses.filter(c => c.bonus).sort((a, b) => a.id - b.id)]] as [string, Course[]][])
    .map(([label, list]): [string, Course[]] => [label, list.filter(c => c.categories.length > 0)]);
  const cells = (c: Course): (SolveTarget | null)[] => categories.map(cat => !c.categories.includes(cat.id) ? null
    : { key: `c:${c.id}:${cat.id}`, body: { course: c.id, capabilities: cat.id }, time: best.get(`${c.id}:${cat.id}`)?.time, farm: farm.get(`${c.id}:${cat.id}`)?.time });
  const open = (t: SolveTarget | null): t is SolveTarget => !!t;
  const all = groups.flatMap(([, list]) => list.flatMap(c => cells(c).filter(open)));
  const queue = all.filter(t => selected.has(t.key));
  const checkbox = (t: SolveTarget) => <label className={`matrixCell ${selected.has(t.key) ? "on" : ""}`} title={t.time != null ? `Current record ${t.time.toFixed(2)}s${t.farm != null ? ` · farm ${t.farm.toFixed(2)}s` : ""}` : "No route yet"}>
    <input type="checkbox" checked={selected.has(t.key)} onChange={() => toggle([t.key])} /><span>{t.time != null ? `${t.time.toFixed(2)}s` : "—"}</span></label>;
  const progress = solver.queue ?? [];
  return <div className="panel offlinePanel solveMatrix"><div className="panelTitle"><div><p className="eyebrow">BATCH SOLVER</p><h2>Optimize many</h2></div><span className={`offlineStatus ${solver.running ? "running" : ""}`}>{solver.running ? progressText(solver) || "Solving" : "Idle"}</span></div>
    <p className="lede">Tick targets to optimize; each one solves in turn like Solve.</p><details className="howTo"><summary>How it works</summary><ol><li>Row or column headers tick whole groups.</li><li>Each target runs up to its minutes, or 5 quiet passes.</li><li>Every gain goes through the same turbo check.</li><li>Loop: each pass doubles minutes, capped at the max.</li><li>Times show the current record, the game&apos;s timer.</li></ol></details>
    <div className="matrixTools">
      <button className="secondary" onClick={() => select(new Set(all.map(t => t.key)))}>All</button>
      <button className="secondary" onClick={() => select(new Set(all.filter(t => t.time != null).map(t => t.key)))} title="Only targets that already have a route">With a time</button>
      <button className="secondary" onClick={() => select(new Set())}>None</button>
    </div>
    <PassFields passes={passes} update={updatePasses} disabled={solver.running} />
    <div className="matrixScroll"><table className="matrix">
      <thead><tr><th />{categories.map((cat, level) => <th key={cat.id}><button title={`${cat.label} — ${describeCaps(cat.caps)}. Ticks every target in this column.`} onClick={() => toggle(groups.flatMap(([, list]) => list.map(c => cells(c)[level]).filter(open).map(t => t.key)))}>{cat.label}</button></th>)}</tr></thead>
      <tbody>
        {groups.map(([label, list]) => list.length > 0 && <Fragment key={label}>
          <tr className="matrixSection"><th colSpan={categories.length + 1}>{label}</th></tr>
          {list.map(c => <tr key={c.id}>
            <th><button title={placeTitle(c)} onClick={() => toggle(cells(c).filter(open).map(t => t.key))}>{courseLabel(c, c.id)}</button></th>
            {cells(c).map((t, level) => <td key={level} className={t ? "" : "na"}>{t && checkbox(t)}</td>)}
          </tr>)}
        </Fragment>)}
      </tbody>
    </table></div>
    <div className="buttonRow">{solver.running
      ? <><button className="secondary" onClick={() => onCommand("/solver/skip")}>Skip current target</button><button className="danger" onClick={() => onCommand("/solver/stop")}>Stop all</button></>
      : <button className="primary" disabled={queue.length === 0} onClick={() => onCommand("/solver/start", { queue: queue.map(t => t.body), ...passes })}>Optimize {queue.length} selected{queue.length === 0 ? "" : passes.repeat ? " · loop until stopped" : ` · up to ${minutesText(queue.length * passes.minutes)}`}</button>}</div>
    {progress.length > 1 && <ol className="matrixQueue">{progress.map((q, i) => <li key={i} className={q.active ? "active" : q.result ? "done" : ""}><strong>{q.label}</strong><span>{q.active ? "Solving…" : q.result ?? "Waiting"}</span></li>)}</ol>}
  </div>;
}

const TICK_SECONDS = 0.02;
const tickText = (ticks: number) => `${ticks} t · ${runTime(ticks * TICK_SECONDS)}`;

function FullTas({fullTas,running,onCommand,onMap}:{fullTas?:FullTasState;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(name:string)=>void}) {
  const runs = fullTas?.runs ?? [];
  const [selected, setSelected] = useState("");
  const [newName, setNewName] = useState("");
  const [minutes, setMinutes] = useState(60);
  const [windowMinutes, setWindowMinutes] = useState(1);
  const [window_, setWindow] = useState(2);
  const [onlyGoal, setOnlyGoal] = useState(0);
  const [onlySegment, setOnlySegment] = useState(0);
  const name = selected || runs[0]?.name || "";
  const run = runs.find(r => r.name === name);
  const savedGoals = (run?.tokens ?? run?.goals ?? []).join("\n");
  const [draft, setDraft] = useState(savedGoals);
  useEffect(() => setDraft(savedGoals), [name, savedGoals]);
  if (!fullTas) return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className="offlineStatus">Unavailable</span></div>
    <p className="lede">The game plugin doesn&apos;t report Full-TAS runs.</p></div>;
  const solve: FullTasSolveState = fullTas.solve ?? { running: false, status: "", log: [] };
  const busy = solve.running || running;
  const goals = draft.split("\n").map(g => g.trim()).filter(g => g);
  const validName = /^[A-Za-z0-9%-]+$/.test(newName);
  const splits = run?.best?.splits ?? run?.offline?.splits ?? [];
  const labels = run?.goals ?? [];
  const liveSegments = solve.name === name ? solve.segments : null;
  const segments = resetSegments(run?.offline?.segments, liveSegments, run?.offline?.faster);
  const segment = onlySegment > 0 && onlySegment <= segments.length ? onlySegment : 0;
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className={`offlineStatus ${busy?"running":""}`}>{solve.running?"Solving":running?"Playing":"Idle"}</span></div>
    <p className="lede">The whole game as one input stream, timed to the last goal.</p><details className="howTo"><summary>Goal syntax</summary><ul><li>One goal per line; reached in order.</li><li><code>start:1</code> · <code>finish:1</code></li><li><code>buy:&lt;box&gt;</code> · <code>reward:bonusN</code></li><li><code>at:x,y,w,h</code> area goal, any entry side.</li><li><code>at:x,y,w,h,from=left+bottom</code> limits entry sides.</li><li><code>"start": "vman"</code> in fulltas.json: Vman world.</li></ul></details><details className="howTo"><summary>How solving works</summary><ol><li><b>Solve</b> searches offline window by window.</li><li>The game replays every better route from its start.</li><li>Runs save only when complete, held up and faster.</li><li>Goals split the run; nothing restarts between them.</li><li>A quick restart after an area goal takes it back.</li><li>Reset segments keep their route unless the start state matches.</li></ol></details>
    <div className="fullTasRuns">
      {runs.map(r => <button key={r.name} className={`fullTasRun ${r.name===name?"selected":""}`} onClick={() => setSelected(r.name)}>
        <strong>{r.name}</strong><small>{r.start === "vman" ? "Vman · " : ""}{r.savestate ? "Savestate · " : ""}{r.goals.length} goal{r.goals.length===1?"":"s"}</small>
        {r.best?<b>{runTime(r.best.time)}</b>:r.offline?<b className="none">offline {r.offline.reached}/{r.goals.length} · {runTime(r.offline.time)}</b>:<b className="none">no route yet</b>}
        {!r.traceReady && <span className="badge missing">No trace</span>}
      </button>)}
      {name && !run && <div className="fullTasRun selected unsaved"><strong>{name}</strong><small>New · save its goals to create it</small></div>}
    </div>
    {!name && <p className="offlineConfirmation">No Full-TAS run yet: name a new one.</p>}
    <div className="fullTasInline">
      <Field label="New run (letters, digits, %, -)"><input value={newName} placeholder="main" onChange={e => setNewName(e.target.value)}/></Field>
      <button className="secondary" disabled={!validName || runs.some(r => r.name === newName)} onClick={() => { setSelected(newName); setNewName(""); }}>+ New run</button>
    </div>
    {name && <div className="fullTasGrid">
      <div className="fullTasGoals">
        <div className="panelTitle"><div><p className="eyebrow">{run?.best?run.best.name:run?.offline?`OFFLINE BEST · ${run.offline.reached}/${run.goals.length} GOALS · UNSAVED`:"NO ROUTE YET"}</p><h2>{name}{run?.best?` · ${runTime(run.best.time)}`:run?.offline?` · ${runTime(run.offline.time)} to goal ${run.offline.reached}`:""}</h2></div></div>
        <div className={`resultTable ${segments.length > 1 ? "segmented" : ""}`}>
          <div className="tableHead"><span>#</span><span>Goal</span><span>Split</span><span>Cumulative</span></div>
          {(() => {
            const goalRow = (i: number) => { const label = labels[i]; const s = splits[i]; const b = run?.breaker; const title = [b!=null&&i===b?"Trips the breaker: the lights go out":"", goalTitle(label) ?? ""].filter(Boolean).join("\n"); return <div className={`tableRow ${s?"":"pending"} ${b==null||i<b?"":i===b?"breakerTrip":"breakerOut"}`} key={i} title={title || undefined}>
              <span>{i + 1}</span><span>{label}</span>
              <span>{s ? tickText(s.ticks - (i > 0 ? splits[i - 1].ticks : 0)) : "—"}</span>
              <span>{s ? tickText(s.ticks) : "—"}</span>
            </div>; };
            if (segments.length <= 1) return labels.map((_, i) => goalRow(i));
            // The best cut at its Quick Restarts: a bar down each segment's goals; goals the best doesn't reach come last. A
            // segment is safe when no buy goal comes from its first goal on: time saved there reaches no Cash check.
            const lastBuy = labels.reduce((at, label, i) => label.startsWith("buy:") ? i : at, -1);
            const rows: ReactElement[] = segments.map((r, k) => {
              const goals: ReactElement[] = [];
              for (let i = r.first; i < r.last && i < labels.length; i++) goals.push(goalRow(i));
              return <SegmentGroup key={`segment${r.n}`} r={r} end={k === segments.length - 1} alt={k % 2 === 1} safe={r.first > lastBuy} selected={segment === r.n} onSelect={setOnlySegment} disabled={solve.running}>{goals}</SegmentGroup>;
            });
            for (let i = segments[segments.length - 1].last; i < labels.length; i++) rows.push(goalRow(i));
            return rows;
          })()}
          {!labels.length && <div className="tableRow pending"><span>—</span><span>No goals</span></div>}
        </div>
        {segments.length > 1 && <SegmentLegend />}
        <div className="fullTasSolve">
          <div className="buttonRow">
            <button className="secondary" disabled={busy || !run} title="Starts a trace and world capture from a fresh game." onClick={() => onCommand("/fulltas/capture", { name })}>Capture start</button>
            <button className="secondary" disabled={busy || !(run?.playable ?? run?.best)} title={run?.best ? "Plays the saved best in real time from a new game." : "Plays the offline best so far; stops at its last goal."} onClick={() => onCommand("/fulltas/play", { name })}>Play</button>
            <button className="secondary" disabled={running || !run?.closest} title={run?.closest ? `Plays the closest attempt (${run.closest.reached}/${run.goals.length} goals, ${tickText(run.closest.ticks)}).` : "No attempt yet; Solve unsolved creates one."} onClick={() => onCommand("/fulltas/play", { name, closest: true })}>Test best</button>
            <button className="danger" disabled={!busy} onClick={() => onCommand("/fulltas/stop")}>Stop</button>
            <label className={`capabilityLock executable ${run && fullTas.goalsOverlay === name ? "on" : ""}`} title="In game: shows this run's goal boxes; reached ones turn green.">
              <input type="checkbox" checked={!!run && fullTas.goalsOverlay === name} disabled={!run} onChange={e => onCommand("/settings/fulltas-goals", { name: e.target.checked ? name : "" })} />
              <strong>Goals in game</strong>
            </label>
          </div>
          <div className="offlineControls">
            <Field label="Minutes (total)"><input type="number" min="1" value={minutes} disabled={solve.running} onChange={e => setMinutes(Number(e.target.value))}/></Field>
            <Field label="Minutes per window"><input type="number" min="0.1" step="0.1" value={windowMinutes} disabled={solve.running} onChange={e => setWindowMinutes(Number(e.target.value))}/></Field>
            <Field label="Window (goals)"><input type="number" min="1" value={window_} disabled={solve.running} onChange={e => setWindow(Number(e.target.value))}/></Field>
            <Field label="Only goal (0: all)"><input type="number" min="0" max={labels.length} value={onlyGoal} disabled={solve.running} title="Polishes only the window ending at this goal; the rest is kept." onChange={e => setOnlyGoal(Math.max(0, Math.min(labels.length, Math.floor(Number(e.target.value)) || 0)))}/></Field>
            <Field label="Only segment (0: all)"><input type="number" min="0" max={segments.length} value={segment} disabled={solve.running || segments.length <= 1} title="Searches only this reset segment; pick one in the list below." onChange={e => setOnlySegment(Math.max(0, Math.min(segments.length, Math.floor(Number(e.target.value)) || 0)))}/></Field>
          </div>
          <div className="buttonRow"><button className="primary" disabled={busy || !run || !run.goals.length} title={onlyGoal > 0 ? `Polishes goals ${Math.max(1, onlyGoal - window_ + 1)}–${onlyGoal} only.` : "Polishes reached goals in windows from goal 1."} onClick={() => onCommand("/fulltas/solve", { name, minutes, windowMinutes, window: window_, goal: onlyGoal })}>{onlyGoal > 0 ? (window_ > 1 ? `Solve goals ${Math.max(1, onlyGoal - window_ + 1)}–${onlyGoal}` : `Solve goal ${onlyGoal}`) : "Solve"}</button>
            <button className="secondary" disabled={busy || !run || !run.goals.length} title="Reaches missing goals first; polishes once all are reached." onClick={() => onCommand("/fulltas/solve", { name, minutes, windowMinutes, window: window_, unsolved: true })}>Solve unsolved</button>
            <button className="secondary" disabled={busy || !run || !run.goals.length || segments.length <= 1} title={segments.length <= 1 ? "The best has no quick restart to split it at" : `Polishes one reset segment at a time${segment ? ` (only segment ${segment})` : ""}.`} onClick={() => onCommand("/fulltas/solve", { name, minutes, windowMinutes, window: window_, resets: true, segment })}>{segment ? `Solve segment ${segment}` : "Solve by resets"}</button>
            <button className="secondary" disabled={!run?.traceReady} title="Draws search windows, best route, goals and avoid boxes." onClick={() => onMap(name)}>Show map</button></div>
        </div>
      </div>
      <div className="fullTasEditor">
        <Field label="Goals (one token per line)"><textarea value={draft} spellCheck={false} placeholder={"start:1\nfinish:1\nbuy:buyreward"} onChange={e => setDraft(e.target.value)}/></Field>
        <div className="buttonRow"><button className="primary" disabled={busy || !goals.length || draft === savedGoals} onClick={() => onCommand("/fulltas/goals", { name, goals })}>Save goals</button>
          <button className="secondary" disabled={draft === savedGoals} onClick={() => setDraft(savedGoals)}>Revert</button></div>
      </div>
    </div>}
    <div className="fullTasSearch">
      <div className="panelTitle"><div><p className="eyebrow">OFFLINE SEARCH</p><h2>{solve.name ? `Solve · ${solve.name}` : "Solve"}</h2></div><span className={`offlineStatus ${solve.running?"running":""}`}>{solve.running?"Running":"Idle"}</span></div>
      <FullTasSolvePanel solve={solve} goals={labels} route={run ? { progress: splits.length, time: splits.length ? splits[splits.length - 1].ticks * TICK_SECONDS : null, splits: splits.map(s => s.ticks * TICK_SECONDS) } : null} run={solve.name ?? name} lastResult={fullTas.lastResult} canPlay={!busy && !!(run?.playable ?? run?.best)} onPlay={() => onCommand("/fulltas/play", { name })}/>
    </div>
  </div>;
}
