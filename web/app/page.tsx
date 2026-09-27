"use client";

import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from "react";
import "./action-editor.css";
import "./frame-timeline.css";
import "./route-sidebar.css";
import "./fulltas.css";
import { FullTasSolvePanel, place, type FullTasOutcome, type FullTasSolveState, type FullTasSplit } from "./fulltas-solve";
import { TechPanel, type TechState } from "./techs";
import { UltraMap, type UltraData } from "./ultras";
import { GROWN_COLOR, MARKERS, SPRING_COLOR, TONE_COLORS, type TreeStep } from "./ultra-level";

const API = "http://127.0.0.1:17426/api";

type Action = { id: string; label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number; enabled: boolean; rawMoveX?: number; rawJumpPressed?: boolean; rawJumpHeld?: boolean; rawJumpReleased?: boolean; rawDashPressed?: boolean; rawDashHeld?: boolean };
type Route = { id: string; name: string; actionCount: number; actions: Action[] };
type RecordedAction = { label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number };
type Recording = { id: string; name: string; createdUtc: string; sampleCount: number; actionCount: number; courseTimeSeconds: number; actions: RecordedAction[] };
type Savestate = { id: string; name: string; createdUtc: string; assigned: boolean };
type Course = { id: number; token: string; bonus: boolean; levels: number[]; from?: string; to?: string; hint?: string | null; optional?: OptionalBlock[] };
type OptionalBlock = { name: string; note: string; on?: boolean; capabilities: string[] };
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; optional?: string[]; evaluations: number; finishes: number; turboChecks: number; turboSaved: number; toValidate?: number; best: number | null; log: string[]; courses: number[]; minutes?: number; repeat?: boolean; pass?: number; queue?: { label: string; result?: string | null; active: boolean }[] };
type FullTasRun = { name: string; start?: string | null; savestate?: string | null; goals: string[]; tokens?: string[]; breaker?: number | null; best: { routeId: string; name: string; time: number; splits: FullTasSplit[] } | null;
  offline?: { reached: number; time: number; splits: FullTasSplit[] } | null; closest?: { reached: number; next: string | null; distance: number | null; ticks: number } | null; playable?: boolean; traceReady: boolean };
type FullTasState = { runs: FullTasRun[]; solve: FullTasSolveState; lastResult?: FullTasOutcome | null; goalsOverlay?: string };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; humanPlayback?: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean; overlayShown: boolean; breakerLightsForced?: boolean; checkpointRespawns?: boolean;
  unlockLevel?: number | null;
  selectedRouteId: string; routes: Route[]; courses?: Course[]; recordings: Recording[]; savestates: Savestate[]; solver: SolverState;
  fullTas?: FullTasState;
  techs?: TechState;
};

const emptyState: StudioState = { connected: false, version: "", engineStatus: "Connecting…", running: false, recording: false, paused: false, capabilitiesLocked: false, overlayShown: true, selectedRouteId: "", routes: [], recordings: [], savestates: [], solver: { running: false, status: "Idle", course: 0, capabilities: "", evaluations: 0, finishes: 0, turboChecks: 0, turboSaved: 0, best: null, log: [], courses: [1, 2, 3] } };

async function request(path: string, body?: unknown) {
  const response = await fetch(`${API}${path}`, { method: body === undefined ? "GET" : "POST", headers: { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (!response.ok || data.error) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}

// Module-level so the Ultra map's load effect doesn't rerun on every render.
const loadUltras = (refresh: boolean): Promise<UltraData> => request("/enginesim/ultras", { refresh });

export default function Studio() {
  const [state, setState] = useState<StudioState>(emptyState);
  const [tab, setTab] = useState("fulltas");
  const [error, setError] = useState("");
  const [mapTarget, setMapTarget] = useState<MapTarget | null>(null);
  const unlockLevel = state.connected ? state.unlockLevel ?? null : null;

  const refresh = useCallback(async () => {
    try { setState(await request("/state")); setError(""); }
    catch { setState(s => ({ ...s, connected: false, engineStatus: "Game plugin offline" })); }
  }, []);

  useEffect(() => { refresh(); const timer = window.setInterval(refresh, 750); return () => window.clearInterval(timer); }, [refresh]);
  const route = useMemo(() => state.routes.find(r => r.id === state.selectedRouteId) ?? state.routes[0], [state]);
  const command = async (path: string, body: unknown = {}) => {
    try { setError(""); await request(path, body); await refresh(); return true; }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); return false; }
  };
  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brandMark">T</span><div><strong>IGTAP</strong><small>TAS STUDIO</small></div></div>
      <div className={`connection ${state.connected ? "online" : "offline"}`}><i />{state.connected ? `Connected · v${state.version}` : "Waiting for game"}</div>
      <RouteSidebar routes={state.routes} courses={state.courses ?? []} activeId={route?.id} unlockLevel={unlockLevel} onCommand={command} />
      <div className="sidebarFooter"><p>The game keeps simulating while this window is focused.</p><kbd>F8</kbd><span>Emergency stop</span></div>
    </aside>

    <section className="workspace">
      <div className="toggleBar">
        <label className={`capabilityLock ${state.capabilitiesLocked ? "on" : ""}`} title={state.capabilitiesLocked ? "On: wall jump / dash on your player are never changed, so your save stays as it is. Course solving and turbo tools need it off; Full-TAS runs work either way (they get their unlocks from the game's own purchases)." : "Off: routes and the solver switch wall jump / dash on your player to match their category, and the game autosaves that."}>
          <input type="checkbox" checked={state.capabilitiesLocked} disabled={!state.connected} onChange={e => command("/settings/lock-capabilities", { locked: e.target.checked })} />
          <strong>Don&apos;t touch my unlocks</strong>
        </label>
        <label className={`capabilityLock executable ${state.overlayShown ? "on" : ""}`} title="In game: the player's hurtbox, spikes with their 2-step grace, the player's velocity, the inputs playback gave on the last ticks (as many as fit on screen), and the air dashes, air jumps and wall jumps left.">
          <input type="checkbox" checked={state.overlayShown} disabled={!state.connected} onChange={e => command("/settings/overlay", { shown: e.target.checked })} />
          <strong>Overlay</strong>
        </label>
        <label className={`capabilityLock executable ${state.breakerLightsForced ? "on" : ""}`} title="In game: Area 1 lit as before the breaker tripped, plus every breaker light. Visual only: the breaker's saved lights and the unlocks they come from don't change, start snapshots (a new game's, a Vman speedrun's) keep the game's own lights, and turning it off shows those again.">
          <input type="checkbox" checked={!!state.breakerLightsForced} disabled={!state.connected} onChange={e => command("/settings/breaker-lights", { on: e.target.checked })} />
          <strong>Breaker lights</strong>
        </label>
        <label className={`capabilityLock executable ${state.checkpointRespawns ? "on" : ""}`} title="The game's pause-menu setting, for every route, trace and search: on, a death on a course respawns at the last checkpoint touched; off, at the course's start. Off a course, deaths and quick restarts go to the last checkpoint either way. Switch it only with nothing playing or solving; the in-game overlay shows it under INTERPOLATION.">
          <input type="checkbox" checked={!!state.checkpointRespawns} disabled={!state.connected || state.running || state.recording || state.solver?.running || state.fullTas?.solve?.running} onChange={e => command("/settings/checkpoint-respawns", { on: e.target.checked })} />
          <strong>Checkpoint respawns</strong>
        </label>
      </div>
      <header className={`topbar ${state.humanPlayback ? "humanized" : ""}`}>
        <div><p className="eyebrow">SELECTED ROUTE</p><h1>{route?.name ?? "No route"}</h1><p className="statusLine"><span className={state.running ? "pulse" : "dot"} />{state.humanPlayback && <span className="humanBadge" title="Playing the human version of an offline best (enginesim humanize): same time, fewer perfect inputs. It is not the best route and is never saved.">HUMANIZED</span>}{state.engineStatus}</p></div>
        <div className="transport">
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/run")}>▶ Run route</button>
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/walk", { slowdown: 10 })} title={"Replay the route with game time slowed 10×, as its human version when one is as fast; the frame rate is unchanged"}>⏵ Walk route</button>
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
        {tab === "timeline" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">FIXED-TICK SEQUENCE</p><h2>Route timeline</h2></div><span className="hint">Changes save immediately</span></div><FrameTimeline actions={route?.actions ?? []}/><div className="timeline">{route?.actions.map((action, index) => <ActionCard key={action.id} action={action} index={index} onSave={(updates) => command("/actions/update", { actionIndex: index, ...updates })} />)}</div></div>}

        {tab === "recordings" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">RAW FIXED-FRAME CAPTURES</p><h2>Recordings</h2></div><span className="hint">{state.recording ? "Recording now…" : `${state.recordings.length} saved`}</span></div>{state.recording&&<p className="recordingActive">● Recording is active. Finishing the course saves it automatically.</p>}<div className="recordingList">{state.recordings.map(recording=><div className="recordingCard" key={recording.id}><div className="recordingTitle"><div><strong>{recording.name}</strong><small>{new Date(recording.createdUtc).toLocaleString()} · {recording.sampleCount} physics frames · {recording.actionCount} actions</small></div>{recording.courseTimeSeconds>0&&<b>{recording.courseTimeSeconds.toFixed(3)}s</b>}</div><p>{recording.actions.map(action=>`${action.type.toLowerCase()} ${DirectionText(action.direction)} ${action.durationTicks}f${action.type==="WALLJUMP"&&action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?` (hold ${DirectionText(action.wallJumpHoldDirection)})`:""}`).join(" → ")}</p><div className="buttonRow"><button className="primary" onClick={()=>command("/recordings/create-route",{recordingId:recording.id})}>Create route</button><button title="Name it Course1_To_Course2 or Course2_To_Bonus2 (optionally _WJ …): a transition route to optimize" onClick={()=>command("/recordings/create-transition",{recordingId:recording.id})}>Create transition</button><button onClick={()=>{const name=window.prompt("Rename recording",recording.name);if(name?.trim())command("/recordings/rename",{recordingId:recording.id,name:name.trim()});}}>Rename</button><button className="danger" onClick={()=>{if(window.confirm(`Delete recording “${recording.name}”?`)) command("/recordings/delete",{recordingId:recording.id});}}>Delete</button></div></div>)}</div>{!state.recordings.length&&!state.recording&&<div className="emptyState"><span className="orbit">●</span><h2>No recordings yet</h2><p>Press Record, play the route, and finish the course.</p></div>}</div>}

        {tab === "fulltas" && <><FullTas fullTas={state.fullTas} running={state.running} onCommand={command} onMap={name => setMapTarget({ course: 0, capabilities: "", optional: [], fulltas: name })}/>{mapTarget?.fulltas && <SolveMap key={`fulltas_${mapTarget.fulltas}`} target={mapTarget} courses={state.courses ?? []} runs={(state.fullTas?.runs ?? []).map(r => r.name)} busy={state.running || !!state.fullTas?.solve.running} onClose={() => setMapTarget(null)}/>}</>}

        {tab === "tech" && <TechPanel techs={state.techs} running={state.running || state.recording} selectedRoute={route} onCommand={command} onEditInputs={async routeId => { if (await command("/routes/select", { routeId })) setTab("timeline"); }}/>}

        {tab === "ultras" && <UltraMap load={loadUltras}/>}

        {tab === "optimize" && <><div className="optimizeGrid"><Solver solver={state.solver ?? emptyState.solver} courses={state.courses ?? []} routes={state.routes} running={state.running} onCommand={command} onMap={setMapTarget}/><SolveMatrix routes={state.routes} courses={state.courses ?? []} solver={state.solver ?? emptyState.solver} onCommand={command}/></div>{mapTarget && <SolveMap key={`${mapTarget.course}_${mapTarget.capabilities}_${mapTarget.optional.join("+")}`} target={mapTarget} courses={state.courses ?? []} onClose={() => setMapTarget(null)}/>}</>}

        {tab === "savestates" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">START STATES</p><h2>Savestates</h2></div><button className="primary" onClick={() => command("/savestates/capture")}>Capture current state</button></div><p className="lede">Assign a savestate to optimize an isolated section. Compatibility fingerprints prevent invalid restores.</p><div className="savestateList">{state.savestates.map(cp => <div className={`savestate ${cp.assigned ? "assigned" : ""}`} key={cp.id}><div><strong>{cp.name}</strong><small>{new Date(cp.createdUtc).toLocaleString()}</small></div>{cp.assigned && <span className="badge">Route start</span>}<button onClick={() => command("/savestates/restore", { savestateId: cp.id })}>Restore</button><button className="secondary" onClick={() => command("/savestates/assign", { savestateId: cp.assigned ? "" : cp.id })}>{cp.assigned ? "Unassign" : "Use as start"}</button></div>)}</div></div>}
      </div>
    </section>
  </main>;
}

const CATEGORIES: [string, string][] = [["NONE", "Nothing"], ["WJ", "Wall jump"], ["WJ_DS", "Wall jump + dash"], ["WJ_DS_DJ", "Wall jump + dash + double jump"], ["WJ_DS_DJ_BS", "Wall jump + dash + double jump + block swap"], ["WJ_DS_DJ_BS_OM", "Wall jump + dash + double jump + block swap + omnidash"]];
const CATEGORY_ROUTE = /^([^_\s]+)_(NONE|WJ_DS_DJ_BS_OM|WJ_DS_DJ_BS|WJ_DS_DJ|WJ_DS|WJ) (farm )?([\d.]+)$/i;

const BONUS_WHERE: Record<string, string> = {
  bonus1: "Below Course 2, a little right of its restart. The run starts right of Course 2, up a bit (where Course 2 → Bonus 1 ends).",
  bonus2: "Between Course 2 and Course 3: below and left of Course 3's start, on the way up. The run starts just left of it.",
  bonus3: "Right of Course 3's restart, a little higher. The run starts at Course 3's restart.",
  bonus4: "Straight below Course 5's start. The run starts at Course 5's restart.",
  bonus5: "Left end of Course 3's upgrade row, a bit below it (the buyfree3 box). The run starts at Course 3's restart.",
  bonus6: "Very high up above Course 3, far left of Course 4 (the buyfree4c box). The run starts at Course 4's restart.",
  bonus7: "Left of Course 4's upgrade row, a little higher (the buyfree4a box). The run starts at Course 4's restart.",
  bonus8: "Far above Course 4 (Course 6's box). The run starts at Course 6's restart.",
  bonus9: "Below and left of Course 9's start. The run starts at Course 6's restart.",
  bonus10: "Left of Course 7's start, a bit lower. The run starts at Course 6's restart.",
  bonus11: "Just below and right of Course 7's start. The run starts at Course 6's restart.",
  bonus12: "Between Course 7 and Course 9: above Course 7, below Course 9. The run starts at Course 6's restart.",
  bonus13: "Below Course 10, a little left. The run starts at Course 10's restart.",
  bonus14: "Right of Course 10. The run starts at Course 10's restart.",
  bonus15: "Far left of Course 5, up high. The run starts at Course 5's restart.",
  bonus16: "Very high above Course 1, level with Bonus 6 (the buyfree4b box). The run starts at Course 4's restart.",
  bonus17: "Far below Course 7 (unlocks jiggle drops, no watts). The run starts at Course 7's restart.",
  bonus18: "Just left of Course 8's start (unlocks clone dust, no watts). The run starts at Course 8's restart.",
  bonus19: "At The Tree in Area 1 (plants the tree, no watts). The run starts at Course 10's restart.",
  bonus20: "The Vman speedrun start in overgrown Area 1, far above Course 10 (no watts). The run starts at Course 10's restart.",
  bonus21: "The other Vman speedrun start, just above Bonus 20 (no watts). The run starts at Course 10's restart.",
  bonus22: "High above Course 5's start, a little left (secret #2, the buyfree5 box; costs watts). The run starts at Course 5's restart.",
  bonus23: "Swap coloured blocks when dashing (buyblockswap5): left end of Course 4's upgrade row, there only after the breaker. The run starts at Course 5's restart.",
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

function categoryBests(routes: Route[], courses: Course[]) {
  const best: Bests = new Map(), farm: Bests = new Map();
  const other: Route[] = [];
  for (const r of routes) {
    const m = CATEGORY_ROUTE.exec(r.name);
    const course = m && courses.find(c => c.token.toLowerCase() === m[1].toLowerCase());
    const level = m ? CATEGORIES.findIndex(([cat]) => cat === m[2].toUpperCase()) : -1;
    if (!m || !course || !course.levels.includes(level)) { other.push(r); continue; }
    const kind = m[3] ? farm : best, key = `${course.id}:${m[2].toUpperCase()}`, time = Number(m[4]);
    const current = kind.get(key);
    if (!current || time < current.time) kind.set(key, {route: r, time});
    else other.push(r);
  }
  return {best, farm, other};
}

const SHORT_CATEGORY: Record<string, string> = { NONE: "None", WJ: "WJ", WJ_DS: "WJ + DS", WJ_DS_DJ: "WJ + DS + DJ", WJ_DS_DJ_BS: "WJ + DS + DJ + BS", WJ_DS_DJ_BS_OM: "WJ + DS + DJ + BS + OM" };

// The best route of this category or of a narrower one the course has (their unlocks are a subset).
function soloBest(best: Bests, c: Course, level: number) {
  let solo: {route: Route; time: number} | undefined;
  for (const narrower of c.levels.filter(l => l <= level).map(l => CATEGORIES[l][0])) {
    const b = best.get(`${c.id}:${narrower}`);
    if (b && (!solo || b.time < solo.time)) solo = b;
  }
  return solo;
}

function RouteSidebar({routes,courses,activeId,unlockLevel,onCommand}:{routes:Route[];courses:Course[];activeId?:string;unlockLevel:number|null;onCommand:(path:string,body?:unknown)=>Promise<boolean>}) {
  const {best, farm, other} = categoryBests(routes, courses);
  const cell = (routeId: string | undefined, text: string, title: string) => routeId
    ? <button className={`cmpCell ${routeId === activeId ? "active" : ""}`} title={title} onClick={() => onCommand("/routes/select", { routeId })}>{text}</button>
    : <span className="cmpCell empty" title={title}>{text}</span>;
  const rows = (c: Course) => c.levels.flatMap(level => {
    const cat = CATEGORIES[level][0];
    const farms = c.bonus || c.to ? best : farm;
    if (!best.has(`${c.id}:${cat}`) && !farms.has(`${c.id}:${cat}`)) return [];
    const record = soloBest(best, c, level), lap = soloBest(farms, c, level);
    const beyond = unlockLevel != null && level > unlockLevel;
    return [<div className={`cmpRow ${beyond ? "beyondUnlocks" : ""}`} key={cat} title={beyond ? `Your save's unlocks only reach ${CATEGORIES[unlockLevel][1]}` : undefined}>
      <span>{SHORT_CATEGORY[cat]}</span>
      {cell(record?.route.id, record ? record.time.toFixed(2) : "—", record?.route.name ?? "No record route")}
      {cell(lap?.route.id, lap ? lap.time.toFixed(2) : "—", lap ? `${lap.route.name}: ${Math.round(lap.time / .02)} physics ticks from the quick restart to the finish` : "No farm route yet")}
    </div>];
  });
  const section = (label: string, list: Course[]) => {
    const groups = list.map(c => ({c, r: rows(c)})).filter(g => g.r.length > 0);
    return groups.length > 0 && <>
      <div className="cmpHead"><label className="sectionLabel">{label}</label><span title="Runs record: the game's timer, start gate to end gate">Record</span><span title="Farm this course: physics ticks from the quick restart to the finish">Farm</span></div>
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
function Solver({solver,courses,routes,running,onCommand,onMap}:{solver:SolverState;courses:Course[];routes:Route[];running:boolean;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(target:MapTarget)=>void}) {
  const [course,setCourse]=useState<number|"">("");
  const [caps,setCaps]=useState("wj");
  const chosen=course===""?(solver.courses[0]??1):course;
  const chosenCourse=courses.find(c=>c.id===chosen);
  const capsOptions=(chosenCourse?chosenCourse.levels:CAPS.map((_,level)=>level)).map(level=>[CAPS[level],CATEGORIES[level][1]]);
  const chosenCaps=capsOptions.some(([v])=>v===caps)?caps:capsOptions[0][0];
  // Names whose checkbox was flipped from its forced-paths.json default ("default": true starts it checked).
  const [optional,setOptional]=useState<string[]>([]);
  const optionalBlocks=(chosenCourse?.optional??[]).filter(b=>b.capabilities.includes(chosenCaps));
  const chosenOptional=optionalBlocks.filter(b=>!!b.on!==optional.includes(b.name)).map(b=>b.name).sort();
  const target={course:chosen,capabilities:chosenCaps,optional:chosenOptional};
  const solo=chosenCourse?soloBest(categoryBests(routes,courses).best,chosenCourse,CAPS.indexOf(chosenCaps)):undefined;
  const runRoute=async()=>{ if(solo&&await onCommand("/routes/select",{routeId:solo.route.id})) await onCommand("/routes/run"); };
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">OFFLINE SOLVER</p><h2>Solve a course</h2></div><span className={`offlineStatus ${solver.running?"running":""}`}>{solver.running?"Solving":"Idle"}</span></div>
    <p className="lede">Captures the course start in the game, then searches offline on every core: it explores new paths from the start and sharpens the best finish; only the start and stop timer gates matter. Every offline best is replayed in the game (turbo) and, if it holds up there, saved as the record route (the game's timer) and, on a course, as the farm route when it takes fewer physics ticks from the quick restart to the finish.</p>
    <div className="offlineControls">
      <Field label="1. Course"><select value={chosen} disabled={solver.running} title={placeTitle(chosenCourse)} onChange={e=>{ if(e.target.value==="new") { let id=1; while(solver.courses.includes(id)) id++; onCommand("/courses/add-from-recording").then(()=>setCourse(id)); } else setCourse(Number(e.target.value)); }}>{solver.courses.map(id=>{ const c=courses.find(c=>c.id===id); return <option key={id} value={id} title={placeTitle(c)}>{courseLabel(c,id)}</option>; })}<option value="new">+ New course from last recording</option></select></Field>
      <Field label="2. Capabilities"><select value={chosenCaps} disabled={solver.running} onChange={e=>setCaps(e.target.value)}>{capsOptions.map(([v,label])=><option key={v} value={v}>{label}</option>)}</select></Field>
      {optionalBlocks.length>0&&<div className="field"><span>Optional avoid blocks</span>{optionalBlocks.map(b=><label key={b.name} className="passLoop" title={b.note}><input type="checkbox" disabled={solver.running} checked={chosenOptional.includes(b.name)} onChange={()=>setOptional(o=>o.includes(b.name)?o.filter(n=>n!==b.name):[...o,b.name])}/>{b.name}</label>)}</div>}
    </div>
    <div className="buttonRow">{solver.running?<button className="danger" onClick={()=>onCommand("/solver/stop")}>Stop</button>:<button className="primary" onClick={()=>onCommand("/solver/start",target)}>3. Solve</button>}<button className="secondary" onClick={()=>onMap(target)} title="Draw what the search has explored for this course and category: saved routes, the offline best, the explored area and the forced-path boxes">Show map</button><button className="secondary" disabled={!solo||running||solver.running} onClick={runRoute} title={solo?`Run ${solo.route.name} in the game`:"No saved route for this course and category"}>▶ Run route</button><button className="secondary" disabled={running||solver.running} onClick={()=>onCommand("/enginesim/play",{course:chosen,capabilities:chosenCaps})} title={"Play this category's newest offline best (the latest solve's) in the game in real time, even one the turbo check rejected; nothing is saved"}>▶ Play offline best</button><button className="secondary humanButton" disabled={running||solver.running} onClick={()=>onCommand("/enginesim/play",{course:chosen,capabilities:chosenCaps,human:true})} title={"Play the human version of this category's newest offline best (enginesim humanize: same time, fewer perfect inputs; its -human.md next to the best lists how much slack each input has) in real time for practice; nothing is saved"}>▶ Play human version</button></div>
    <div className="offlineSummary"><div><span>Best</span><strong>{solver.best!=null?`${solver.best.toFixed(2)}s`:"—"}</strong><small>{solver.course?`${courseLabel(courses.find(c=>c.id===solver.course),solver.course)} · ${solver.capabilities}${solver.optional?.length?` +${solver.optional.join(" +")}`:""}`:"Not started"}</small></div><div><span>Runs</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.finishes.toLocaleString()} timed finishes</small></div><div><span>Turbo checks</span><strong>{solver.turboChecks.toLocaleString()}</strong><small>{solver.turboSaved.toLocaleString()} saved as routes</small></div><div><span>To validate</span><strong>{(solver.toValidate??0).toLocaleString()}</strong><small>offline bests waiting for a turbo check</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.log.length>0&&<ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul>}
  </div>;
}

type MapTarget = { course: number; capabilities: string; optional: string[]; fulltas?: string };
type MapSolid = { k: "poly" | "line" | "circle" | "capsule"; spike: boolean; p: number[]; r: number; tone?: "blue" | "orange" | null; spring?: number[] | null };
type MapRoute = { name: string; kind: "seed" | "best" | "plateau"; finished: boolean; time: number; timerStart: number; points: number[] };
// Every map coordinate is in the game world's frame (courses.json's, forced-paths.json's, --via/--avoid's, at:x,y goals'):
// the same for every capture of a place, whatever FloatingOrigin the game had then.
type MapData = { course: number; capabilities: string; label: string; fulltas?: string; bounds: number[]; solids: MapSolid[];
  // The trees' ground where it grows once its tree upgrades are bought (not in the world the run starts in).
  grown?: MapSolid[]; treeSteps?: TreeStep[];
  markers: { kind: string; name: string; x: number; y: number }[]; avoid: number[][]; via: number[][]; goals?: MapGoal[]; optional?: { name: string; note: string; on: boolean; avoid: number[][]; via: number[][] }[]; routes: MapRoute[];
  explored: { cell: number; runs: number; prefixes: number; cells: number[][] } };
// A Full-TAS run's at: goal: its number in the run's goal list (1-based, as the goals table), its label and its rect (x0, y0, x1, y1).
type MapGoal = { index: number; label: string; box: number[] };
type MapLayers = { explored: boolean; boxes: boolean; markers: boolean };
const ROUTE_COLORS = ["#4fc3f7", "#ff8a65", "#ba68c8", "#81c784", "#f06292", "#fff176", "#4db6ac", "#9575cd"];

function SolveMap({target,courses,runs,busy,onClose}:{target:MapTarget;courses:Course[];runs?:string[];busy?:boolean;onClose:()=>void}) {
  const [data, setData] = useState<MapData | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState("");
  const [layers, setLayers] = useState<MapLayers>({ explored: true, boxes: true, markers: true });
  const [hidden, setHidden] = useState<Set<number>>(new Set());
  const [cursor, setCursor] = useState<{ x: number; y: number; marker?: string; reached?: number } | null>(null);
  const arrivals = useMemo(() => new Map(data?.explored.cells.map(([cx, cy, t]) => [`${cx},${cy}`, t] as [string, number]) ?? []), [data]);
  const latest = useMemo(() => data?.explored.cells.reduce((m, v) => Math.max(m, v[2]), 0) || 1, [data]);
  const canvas = useRef<HTMLCanvasElement>(null);
  // Full-TAS: an avoid box drawn on the map (world x0, y0, x1, y1 as dragged) and the runs it goes to.
  const [drawing, setDrawing] = useState(false);
  const [sketch, setSketch] = useState<number[] | null>(null);
  const [sketchRuns, setSketchRuns] = useState<string[]>(target.fulltas ? [target.fulltas] : []);
  const [saving, setSaving] = useState(false);
  const sketchStart = useRef<{ x: number; y: number } | null>(null);
  // Full-TAS: an at: goal dragged to a new place (same size), shown there until it's saved or discarded.
  const [moving, setMoving] = useState(false);
  const [moved, setMoved] = useState<{ goal: MapGoal; box: number[] } | null>(null);
  const grab = useRef<{ goal: MapGoal; box: number[]; x: number; y: number } | null>(null);
  const colors = useMemo(() => data?.routes.map((r, i) => r.kind === "best" ? "#ffd54f" : r.kind === "plateau" ? "rgba(255,255,255,.55)" : ROUTE_COLORS[i % ROUTE_COLORS.length]) ?? [], [data]);
  const view = useRef({ ox: 0, oy: 0, scale: 1 });
  const drag = useRef<{ x: number; y: number } | null>(null);
  const fitted = useRef(false);

  const load = useCallback(async () => {
    setLoading(true); setFailure("");
    try { const d: MapData = target.fulltas ? await request("/fulltas/map", { name: target.fulltas }) : await request("/enginesim/map", target); setData(d); setHidden(new Set(d.routes.map((r, i) => r.kind === "plateau" ? i : -1).filter(i => i >= 0))); if (!fitted.current) { fit(d); fitted.current = true; } }
    catch (e) { setFailure(e instanceof Error ? e.message : String(e)); }
    finally { setLoading(false); }
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
    if (layers.explored && data.explored.cells.length) {
      const size = data.explored.cell;
      for (const [cx, cy, t] of data.explored.cells) {
        g.fillStyle = t < 0 ? "#34383f" : `hsl(${230 - 230 * t / latest},90%,55%)`;
        g.fillRect(cx * size, cy * size, size, size);
      }
    }
    for (const s of data.solids) {
      const color = s.spring ? SPRING_COLOR : s.tone ? TONE_COLORS[s.tone] : s.spike ? "#e5484d" : "#8193ab";
      g.fillStyle = color; g.strokeStyle = color; g.lineWidth = Math.max(px * 1.5, s.k === "capsule" ? s.r * 2 : 0); g.lineCap = "round";
      g.beginPath();
      if (s.k === "circle") { g.arc(s.p[0], s.p[1], s.r, 0, Math.PI * 2); g.fill(); continue; }
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
    if (layers.markers) for (const m of data.markers) {
      g.fillStyle = m.kind === "goal" ? "#b9f6ca" : MARKERS[m.kind]?.color ?? "#4fc3f7";
      const s = (m.kind === "checkpoint" ? 4 : 6) * px;
      if (MARKERS[m.kind]?.shape === "circle") { g.beginPath(); g.arc(m.x, m.y, s, 0, Math.PI * 2); g.fill(); }
      else if (m.kind === "teleporter") { g.beginPath(); g.moveTo(m.x, m.y + s * 1.3); g.lineTo(m.x + s, m.y); g.lineTo(m.x, m.y - s * 1.3); g.lineTo(m.x - s, m.y); g.closePath(); g.fill(); }
      else g.fillRect(m.x - s, m.y - s, 2 * s, 2 * s);
    }
  }, [data, layers, hidden, colors, latest, sketch, moved]);
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
    if (drag.current) { v.ox -= (e.clientX - drag.current.x) / v.scale; v.oy += (e.clientY - drag.current.y) / v.scale; drag.current = { x: e.clientX, y: e.clientY }; draw(); }
    const { x, y } = world(e), reach = 10 / v.scale;
    if (sketchStart.current) setSketch([sketchStart.current.x, sketchStart.current.y, x, y]);
    const held = grab.current;
    if (held) { const dx = x - held.x, dy = y - held.y; setMoved({ goal: held.goal, box: [held.box[0] + dx, held.box[1] + dy, held.box[2] + dx, held.box[3] + dy] }); }
    const near = layers.markers ? data?.markers.find(m => Math.abs(m.x - x) < reach && Math.abs(m.y - y) < reach) : undefined;
    const size = data?.explored.cell ?? 1;
    setCursor({ x, y, marker: near ? `${near.name} (${near.kind})` : undefined, reached: arrivals.get(`${Math.floor(x / size)},${Math.floor(y / size)}`) });
  };
  const editAvoid = async (box: number[], names: string[], remove: boolean) => {
    setSaving(true); setFailure("");
    try { await request("/fulltas/avoid", { box, runs: names, remove }); if (!remove) setSketch(null); await load(); }
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
      ? <p className="lede">Where this run's search windows have gone (every solve of its current goals, from <code>archive\fulltas-{target.fulltas}.visits.json</code>), colored by the run time a window first got there (blue early, red late); the best so far (gold) replayed from the run's start; its <code>at:</code> goals as they are in <code>fulltas.json</code> now (green boxes, numbered markers) and its avoid boxes (red: forced-paths.json rules with <code>"fulltas": ["{target.fulltas}"]</code>; a window run entering one is cut, from the next window of a running solve). <strong>Draw avoid</strong>, then drag a box on the map to add one. <strong>Move goals</strong>, then drag a green goal box (same size) or type its new centre, and save it: the run's goals change as with <b>Save goals</b>. Coordinates are game-world ones, the frame of forced-paths.json and <code>at:</code> goals. Wheel to zoom, drag to pan.</p>
      : <p className="lede">Replayed offline from the course start of the last solve: the level as the solver sees it (its world export, with the unlocks and blocks of that moment), the explored area colored by the fastest time any explored run reached each spot with the timer going (blue early, red late; grey: only reached with the timer off, before the start gate or after a run was cancelled), saved routes, the fastest offline best (gold) and forced-path boxes (red: runs entering them are cut; dashed: an optional block that is off). A ring marks where a route's timer starts. Coordinates are game-world ones, the frame of forced-paths.json, <code>--via</code>/<code>--avoid</code>, <code>at:</code> goals and courses.json. Wheel to zoom, drag to pan.</p>}
    {failure && <p className="mapFailure">{failure}</p>}
    <div className="mapLayers">
      {([["explored", "Explored"], ["boxes", "Forced-path boxes"], ["markers", "Gates, checkpoints, teleporters, refills"]] as [keyof MapLayers, string][]).map(([key, label]) =>
        <label key={key} className="passLoop"><input type="checkbox" checked={layers[key]} onChange={() => toggle(key)}/>{label}</label>)}
      {data && <span className="hint">{data.fulltas ? `${data.explored.cells.length.toLocaleString()} explored cells` : `${data.explored.prefixes.toLocaleString()} explore prefixes · ${data.explored.runs.toLocaleString()} runs`}</span>}
      {data && <span className="hint">{data.fulltas ? "run time" : "timer"} at first arrival: <span style={{ display: "inline-block", width: 90, height: 8, verticalAlign: "middle", background: "linear-gradient(90deg,hsl(230,90%,55%),hsl(172,90%,55%),hsl(115,90%,55%),hsl(57,90%,55%),hsl(0,90%,55%))" }}/> 0–{latest.toFixed(1)}s{!data.fulltas && <> · <span style={{ display: "inline-block", width: 8, height: 8, background: "#34383f", verticalAlign: "middle" }}/> timer off</>}</span>}
      {data?.fulltas && <button className={drawing ? "primary" : "secondary"} disabled={saving} title="Drag a box on the map: runs of the search that enter it are cut" onClick={() => { setDrawing(d => !d); setSketch(null); setMoving(false); }}>{drawing ? "Drag a box…" : "Draw avoid"}</button>}
      {data?.fulltas && <button className={moving ? "primary" : "secondary"} disabled={saving} title="Drag an at: goal's green box to a new place; it keeps its size" onClick={() => { setMoving(m => !m); setMoved(null); setDrawing(false); setSketch(null); }}>{moving ? "Drag a goal…" : "Move goals"}</button>}
    </div>
    <canvas ref={canvas} className="mapCanvas" style={drawing ? { cursor: "crosshair" } : moving ? { cursor: "move" } : undefined} onMouseDown={onDown} onMouseUp={onUp} onMouseLeave={() => { onUp(); setCursor(null); }} onMouseMove={onMove}/>
    <div className="mapReadout">{cursor ? <>world {cursor.x.toFixed(1)}, {cursor.y.toFixed(1)}{cursor.reached != null && <> · {cursor.reached < 0 ? "reached only with the timer off" : `first reached at ${cursor.reached.toFixed(2)}s`}</>}{cursor.marker && <> · <strong>{cursor.marker}</strong></>}</> : "Point at the map for coordinates."}</div>
    {sketch && !sketchStart.current && <div className="buttonRow">
      <span className="hint">Avoid {boxText(sketch)} for</span>
      {(runs?.length ? runs : sketchRuns).map(n => <label key={n} className="passLoop"><input type="checkbox" checked={sketchRuns.includes(n)} onChange={() => setSketchRuns(r => r.includes(n) ? r.filter(k => k !== n) : [...r, n])}/>{n}</label>)}
      <button className="primary" disabled={saving || !sketchRuns.length} onClick={() => editAvoid(sketch, sketchRuns, false)}>Add avoid</button>
      <button className="secondary" disabled={saving} onClick={() => setSketch(null)}>Discard</button></div>}
    {moved && <div className="buttonRow">
      <span className="hint">Goal {moved.goal.index} <code>{moved.goal.label}</code>: centre {center(moved.goal.box).x}, {center(moved.goal.box).y} to</span>
      <Field label="x"><input type="number" step="0.1" value={center(moved.box).x} onChange={e => placeMoved(Number(e.target.value), center(moved.box).y)}/></Field>
      <Field label="y"><input type="number" step="0.1" value={center(moved.box).y} onChange={e => placeMoved(center(moved.box).x, Number(e.target.value))}/></Field>
      <button className="primary" disabled={saving || busy} title={busy ? "Stop the solve or playback first (as Save goals)" : "Saves the goal here in fulltas.json"} onClick={saveMoved}>Save goal</button>
      <button className="secondary" disabled={saving} onClick={() => setMoved(null)}>Discard</button></div>}
    {data?.fulltas && data.avoid.length > 0 && <ul className="mapRoutes">{data.avoid.map((b, i) => <li key={i}><span className="mapSwatch" style={{ background: "#ff3030" }}/>avoid {boxText(b)}
      <button className="secondary" disabled={saving} title={`Takes this box out of every forced-paths.json rule of ${data.fulltas} (a box shared with other runs goes for them too)`} onClick={() => editAvoid(b, [data.fulltas!], true)}>Remove</button></li>)}</ul>}
    {data && data.routes.length > 0 && <ul className="mapRoutes">{data.routes.map((r, i) => <li key={i}><label className="passLoop"><input type="checkbox" checked={!hidden.has(i)} onChange={() => setHidden(h => { const n = new Set(h); if (n.has(i)) n.delete(i); else n.add(i); return n; })}/>
      <span className="mapSwatch" style={{ background: colors[i] }}/>{r.name}<small>{r.finished ? `${r.time.toFixed(2)}s` : "no finish"} · {r.points.length / 2} ticks</small></label></li>)}</ul>}
  </div>;
}

const CAPS = ["none", "wj", "wjds", "wjdsdj", "wjdsdjbs", "wjdsdjbsom"];
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
    <label className="passLoop" title="When the last target ends, start over from the first, each pass with twice the minutes (up to the max): the run gets quick passes first, deeper ones after. Runs until Stop."><input type="checkbox" disabled={disabled} checked={passes.repeat} onChange={e => update({ repeat: e.target.checked })} /><span>Loop until stopped</span></label>
    <Field label="Max minutes per target"><input type="number" min={0.25} step={0.25} disabled={disabled || !passes.repeat} value={passes.maxMinutes} onChange={e => update({ maxMinutes: clamp(e.target.value) })} /></Field>
  </div>;
}

function progressText(solver: SolverState) {
  const queue = solver.queue ?? [], at = queue.findIndex(q => q.active);
  return [solver.repeat ? `Pass ${solver.pass ?? 1}` : "", at >= 0 && queue.length > 1 ? `target ${at + 1} of ${queue.length}` : "", solver.minutes ? `${minutesText(solver.minutes)} each` : ""].filter(Boolean).join(" · ");
}

function SolveMatrix({routes,courses,solver,onCommand}:{routes:Route[];courses:Course[];solver:SolverState;onCommand:(path:string,body?:unknown)=>Promise<boolean>}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [passes, updatePasses] = usePasses();
  useEffect(() => setSelected(new Set(JSON.parse(window.localStorage.getItem("optimizeSelection") ?? "[]"))), []);
  const select = (next: Set<string>) => { setSelected(next); window.localStorage.setItem("optimizeSelection", JSON.stringify([...next])); };
  const toggle = (keys: string[]) => {
    const next = new Set(selected), on = keys.some(k => !next.has(k));
    for (const k of keys) { if (on) next.add(k); else next.delete(k); }
    select(next);
  };
  const {best, farm} = categoryBests(routes, courses);
  const groups: [string, Course[]][] = [
    ["COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id)],
    ["TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id)],
    ["BONUSES", courses.filter(c => c.bonus).sort((a, b) => a.id - b.id)]];
  const cells = (c: Course): (SolveTarget | null)[] => CATEGORIES.map(([cat], level) => !c.levels.includes(level) ? null
    : { key: `c:${c.id}:${CAPS[level]}`, body: { course: c.id, capabilities: CAPS[level] }, time: best.get(`${c.id}:${cat}`)?.time, farm: farm.get(`${c.id}:${cat}`)?.time });
  const open = (t: SolveTarget | null): t is SolveTarget => !!t;
  const all = groups.flatMap(([, list]) => list.flatMap(c => cells(c).filter(open)));
  const queue = all.filter(t => selected.has(t.key));
  const checkbox = (t: SolveTarget) => <label className={`matrixCell ${selected.has(t.key) ? "on" : ""}`} title={t.time != null ? `Current record ${t.time.toFixed(2)}s${t.farm != null ? ` · farm ${t.farm.toFixed(2)}s` : ""}` : "No route yet"}>
    <input type="checkbox" checked={selected.has(t.key)} onChange={() => toggle([t.key])} /><span>{t.time != null ? `${t.time.toFixed(2)}s` : "—"}</span></label>;
  const progress = solver.queue ?? [];
  return <div className="panel offlinePanel solveMatrix"><div className="panelTitle"><div><p className="eyebrow">BATCH SOLVER</p><h2>Optimize many</h2></div><span className={`offlineStatus ${solver.running ? "running" : ""}`}>{solver.running ? progressText(solver) || "Solving" : "Idle"}</span></div>
    <p className="lede">Tick what to optimize (a row or column header ticks all of it). Each target is solved in turn exactly like Solve, for up to its minutes or until half of them (at most 5) pass without a new best; every gain goes through the same turbo check and gate. Times are the current record (the game's timer).</p>
    <div className="matrixTools">
      <button className="secondary" onClick={() => select(new Set(all.map(t => t.key)))}>All</button>
      <button className="secondary" onClick={() => select(new Set(all.filter(t => t.time != null).map(t => t.key)))} title="Only targets that already have a route">With a time</button>
      <button className="secondary" onClick={() => select(new Set())}>None</button>
    </div>
    <PassFields passes={passes} update={updatePasses} disabled={solver.running} />
    <div className="matrixScroll"><table className="matrix">
      <thead><tr><th />{CATEGORIES.map(([cat, title], level) => <th key={cat}><button title={`Tick every ${title.toLowerCase()} target`} onClick={() => toggle(groups.flatMap(([, list]) => list.map(c => cells(c)[level]).filter(open).map(t => t.key)))}>{cat}</button></th>)}</tr></thead>
      <tbody>
        {groups.map(([label, list]) => list.length > 0 && <Fragment key={label}>
          <tr className="matrixSection"><th colSpan={CATEGORIES.length + 1}>{label}</th></tr>
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
const tickText = (ticks: number) => `${ticks} t · ${(ticks * TICK_SECONDS).toFixed(2)}s`;

function FullTas({fullTas,running,onCommand,onMap}:{fullTas?:FullTasState;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<boolean>;onMap:(name:string)=>void}) {
  const runs = fullTas?.runs ?? [];
  const [selected, setSelected] = useState("");
  const [newName, setNewName] = useState("");
  const [minutes, setMinutes] = useState(60);
  const [windowMinutes, setWindowMinutes] = useState(1);
  const [window_, setWindow] = useState(2);
  const [onlyGoal, setOnlyGoal] = useState(0);
  const name = selected || runs[0]?.name || "";
  const run = runs.find(r => r.name === name);
  const savedGoals = (run?.tokens ?? run?.goals ?? []).join("\n");
  const [draft, setDraft] = useState(savedGoals);
  useEffect(() => setDraft(savedGoals), [name, savedGoals]);
  if (!fullTas) return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className="offlineStatus">Unavailable</span></div>
    <p className="lede">The game plugin doesn&apos;t report Full-TAS runs (an older version, or not connected).</p></div>;
  const solve: FullTasSolveState = fullTas.solve ?? { running: false, status: "", log: [] };
  const busy = solve.running || running;
  const goals = draft.split("\n").map(g => g.trim()).filter(g => g);
  const validName = /^[A-Za-z0-9%-]+$/.test(newName);
  const splits = run?.best?.splits ?? run?.offline?.splits ?? [];
  const labels = run?.goals ?? [];
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className={`offlineStatus ${busy?"running":""}`}>{solve.running?"Solving":running?"Playing":"Idle"}</span></div>
    <p className="lede">A Full-TAS run is the whole game from a fresh new game (or, with <code>"start": "vman"</code> in <code>fulltas.json</code>, from a Vman speedrun's world) as one continuous input stream, timed from its first tick to its last goal. Goals (one token per line: <code>start:1</code>, <code>finish:1</code>, <code>buy:buyreward</code>, <code>reward:bonus1</code>, or an area goal <code>at:x,y,width,height</code>, optionally entered only from given sides: <code>at:x,y,width,height,from=left+bottom</code>) are reached in order; they only split the run, nothing restarts between them (a quick restart is an input the route chooses, but one right after an area goal takes that goal back). <b>Solve</b> searches offline window by window; the game replays every better route from its start and saves it only when it holds up and is faster.</p>
    <div className="fullTasRuns">
      {runs.map(r => <button key={r.name} className={`fullTasRun ${r.name===name?"selected":""}`} onClick={() => setSelected(r.name)}>
        <strong>{r.name}</strong><small>{r.start === "vman" ? "Vman · " : ""}{r.savestate ? "Savestate · " : ""}{r.goals.length} goal{r.goals.length===1?"":"s"}</small>
        {r.best?<b>{r.best.time.toFixed(2)}s</b>:r.offline?<b className="none">offline {r.offline.reached}/{r.goals.length} · {r.offline.time.toFixed(2)}s</b>:<b className="none">no route yet</b>}
        <span className={`badge ${r.traceReady?"":"missing"}`}>{r.traceReady?"Trace ready":"No trace"}</span>
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
        <div className="panelTitle"><div><p className="eyebrow">{run?.best?run.best.name:run?.offline?`OFFLINE BEST · ${run.offline.reached} OF ${run.goals.length} GOALS · NOT SAVED (ONLY COMPLETE RUNS GO TO THE GATE)`:"NO ROUTE YET"}</p><h2>{name}{run?.best?` · ${run.best.time.toFixed(2)}s`:run?.offline?` · ${run.offline.time.toFixed(2)}s to goal ${run.offline.reached}`:""}</h2></div></div>
        <div className="resultTable">
          <div className="tableHead"><span>#</span><span>Goal</span><span>Split</span><span>Cumulative</span></div>
          {labels.map((label, i) => { const s = splits[i]; const b = run?.breaker; const title = [b!=null&&i===b?"Trips the breaker: the lights go out":"", goalTitle(label) ?? ""].filter(Boolean).join("\n"); return <div className={`tableRow ${s?"":"pending"} ${b==null||i<b?"":i===b?"breakerTrip":"breakerOut"}`} key={i} title={title || undefined}>
            <span>{i + 1}</span><span>{label}</span>
            <span>{s ? tickText(s.ticks - (i > 0 ? splits[i - 1].ticks : 0)) : "—"}</span>
            <span>{s ? tickText(s.ticks) : "—"}</span>
          </div>; })}
          {!labels.length && <div className="tableRow pending"><span>—</span><span>No goals</span></div>}
        </div>
        <div className="fullTasSolve">
          <div className="buttonRow">
            <button className="secondary" disabled={busy || !run} title="Starts a trace and world capture from a fresh new game (seeded with the saved best's inputs)" onClick={() => onCommand("/fulltas/capture", { name })}>Capture start</button>
            <button className="secondary" disabled={busy || !(run?.playable ?? run?.best)} title={run?.best ? "Plays the saved best in real time from a new game" : "Plays the offline search's best so far in real time from a new game (it stops at the last goal it reaches)"} onClick={() => onCommand("/fulltas/play", { name })}>Play</button>
            <button className="secondary" disabled={running || !run?.closest} title={run?.closest ? `Plays the search's attempt furthest toward the goals so far (${run.closest.reached} of ${run.goals.length} reached${run.closest.next ? `, next ${run.closest.next}` : ""}${run.closest.distance != null ? `, ${run.closest.distance} away` : ""}; ${tickText(run.closest.ticks)}) in real time from a new game, ending where the attempt ends. Works while a solve runs: stop it with the header's Stop or F8 (this panel's Stop ends the solve too).` : "No search attempt for these goals yet: Solve unsolved writes the attempt that gets closest to the next goal"} onClick={() => onCommand("/fulltas/play", { name, closest: true })}>Test best</button>
            <button className="danger" disabled={!busy} onClick={() => onCommand("/fulltas/stop")}>Stop</button>
            <label className={`capabilityLock executable ${run && fullTas.goalsOverlay === name ? "on" : ""}`} title="In game: this run's area goal (at:) and bonus end (reward:) rects, numbered, where the goal check tests them. While the run plays, reached goals turn green and the next one yellow.">
              <input type="checkbox" checked={!!run && fullTas.goalsOverlay === name} disabled={!run} onChange={e => onCommand("/settings/fulltas-goals", { name: e.target.checked ? name : "" })} />
              <strong>Goals in game</strong>
            </label>
          </div>
          <div className="offlineControls">
            <Field label="Minutes (total)"><input type="number" min="1" value={minutes} disabled={solve.running} onChange={e => setMinutes(Number(e.target.value))}/></Field>
            <Field label="Minutes per window"><input type="number" min="0.1" step="0.1" value={windowMinutes} disabled={solve.running} onChange={e => setWindowMinutes(Number(e.target.value))}/></Field>
            <Field label="Window (goals)"><input type="number" min="1" value={window_} disabled={solve.running} onChange={e => setWindow(Number(e.target.value))}/></Field>
            <Field label="Only goal (0: all)"><input type="number" min="0" max={labels.length} value={onlyGoal} disabled={solve.running} title="Polish only the window of goals ending at this one (Window goals: 2 is the goal before it and this one), over and over, with the rest of the route kept as it is" onChange={e => setOnlyGoal(Math.max(0, Math.min(labels.length, Math.floor(Number(e.target.value)) || 0)))}/></Field>
          </div>
          <div className="buttonRow"><button className="primary" disabled={busy || !run || !run.goals.length} title={onlyGoal > 0 ? `Polishes only goals ${Math.max(1, onlyGoal - window_ + 1)}–${onlyGoal}, from ${onlyGoal - window_ > 0 ? "goal " + (onlyGoal - window_) : "the start"}` : "Polishes the goals the best reaches, windows from goal 1 on (captures the start first if there's no trace or the goals changed)"} onClick={() => onCommand("/fulltas/solve", { name, minutes, windowMinutes, window: window_, goal: onlyGoal })}>{onlyGoal > 0 ? (window_ > 1 ? `Solve goals ${Math.max(1, onlyGoal - window_ + 1)}–${onlyGoal}` : `Solve goal ${onlyGoal}`) : "Solve"}</button>
            <button className="secondary" disabled={busy || !run || !run.goals.length} title="Reaches the goals the best doesn't reach yet (no time), starting at the first of them; polishes once every goal is reached" onClick={() => onCommand("/fulltas/solve", { name, minutes, windowMinutes, window: window_, unsolved: true })}>Solve unsolved</button>
            <button className="secondary" disabled={!run?.traceReady} title="Draws where this run's search windows have gone, its best so far, its at: goals and its avoid boxes (forced-paths.json)" onClick={() => onMap(name)}>Show map</button></div>
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
