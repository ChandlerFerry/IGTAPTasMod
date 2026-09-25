"use client";

import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
import "./action-editor.css";
import "./frame-timeline.css";
import "./favorite-ui.css";
import "./route-sidebar.css";
import "./fulltas.css";

const API = "http://127.0.0.1:17426/api";

type Action = { id: string; label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number; enabled: boolean; rawMoveX?: number; rawMoveY?: number; rawJumpPressed?: boolean; rawJumpHeld?: boolean; rawJumpReleased?: boolean; rawDashPressed?: boolean; rawDashHeld?: boolean };
type Route = { id: string; name: string; favorite: boolean; hotkey: string; checkpointId: string; actionCount: number; actions: Action[] };
type RecordedAction = { label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number };
type Recording = { id: string; name: string; favorite: boolean; createdUtc: string; sampleCount: number; actionCount: number; courseTimeSeconds: number; actions: RecordedAction[] };
type Checkpoint = { id: string; name: string; createdUtc: string; assigned: boolean };
type Result = { id: string; success: boolean; completionTime: number; completionTicks: number; timingSource?: string; deterministicPhysics?: boolean; failureReason?: string; timestampUtc: string };
// A bonus has a reward box instead of gates; a transition runs between two places: a course ("2") or a bonus ("bonus2").
type Course = { id: number; token: string; bonus: boolean; minLevel: number; from?: string; to?: string };
// `queue`: the run's targets in order (one for a single Solve), each with how it ended once it has. `repeat`: the queue
// starts over when it ends (pass 2, 3, ...), `minutes` doubling each pass, until stopped.
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; evaluations: number; finishes: number; turboChecks: number; turboSaved: number; toValidate?: number; best: number | null; log: string[]; courses: number[]; minutes?: number; repeat?: boolean; pass?: number; queue?: { label: string; result?: string | null; active: boolean }[] };
// A Full-TAS run: the whole game from a new game as one input stream, split by its goals (config tokens, "start:1").
// A split's `ticks` are the physics ticks from the run's start when that goal was reached.
type FullTasSplit = { label: string; ticks: number };
// `offline`: the offline search's best so far (partial runs too, which never reach the gate); `playable`: Play has
// something to play (the saved best, else the offline best, else the run's seed).
type FullTasRun = { name: string; goals: string[]; tokens?: string[]; best: { routeId: string; name: string; time: number; splits: FullTasSplit[] } | null;
  offline?: { reached: number; time: number; splits: FullTasSplit[] } | null; playable?: boolean; traceReady: boolean };
// The gate's reply to a submit (the plugin's `lastResult` too).
type FullTasReply = { goals?: unknown; finished?: boolean; time?: number | null; saved?: boolean; reason?: string | null; splits?: FullTasSplit[]; trace?: string };
type FullTasState = { runs: FullTasRun[]; solve: { running: boolean; name?: string | null; status: string; log: string[] }; lastResult?: FullTasReply | string | null };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean; overlayShown: boolean;
  // The widest category the save's own unlocks can run (0 NO_WJ … 3 WJ_DASH_AJ); null when the player isn't loaded.
  unlockLevel?: number | null;
  selectedRouteId: string; routes: Route[]; courses?: Course[]; recordings: Recording[]; checkpoints: Checkpoint[]; results: Result[]; solver: SolverState;
  // Missing on a plugin without Full-TAS.
  fullTas?: FullTasState;
};

const emptyState: StudioState = { connected: false, version: "", engineStatus: "Connecting…", running: false, recording: false, paused: false, capabilitiesLocked: false, overlayShown: true, selectedRouteId: "", routes: [], recordings: [], checkpoints: [], results: [], solver: { running: false, status: "Idle", course: 0, capabilities: "", evaluations: 0, finishes: 0, turboChecks: 0, turboSaved: 0, best: null, log: [], courses: [1, 2, 3] } };

async function request(path: string, body?: unknown) {
  const response = await fetch(`${API}${path}`, { method: body === undefined ? "GET" : "POST", headers: { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (!response.ok || data.error) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}

export default function Studio() {
  const [state, setState] = useState<StudioState>(emptyState);
  const [tab, setTab] = useState("overview");
  const [error, setError] = useState("");
  const unlockLevel = state.connected ? state.unlockLevel ?? null : null;

  const refresh = useCallback(async () => {
    try { setState(await request("/state")); setError(""); }
    catch { setState(s => ({ ...s, connected: false, engineStatus: "Game plugin offline" })); }
  }, []);

  useEffect(() => { refresh(); const timer = window.setInterval(refresh, 750); return () => window.clearInterval(timer); }, [refresh]);
  const route = useMemo(() => state.routes.find(r => r.id === state.selectedRouteId) ?? state.routes[0], [state]);
  const command = async (path: string, body: unknown = {}) => {
    try { setError(""); await request(path, body); await refresh(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  };
  const successes = state.results.filter(r => r.success);
  const successRate = state.results.length ? successes.length / state.results.length * 100 : 0;
  const bestResult = successes.length ? successes.reduce((best,current)=>current.completionTime<best.completionTime?current:best) : null;

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
        <label className={`capabilityLock executable ${state.overlayShown ? "on" : ""}`} title="In game: the player's hurtbox, spikes with their 2-step grace, and the inputs playback presses.">
          <input type="checkbox" checked={state.overlayShown} disabled={!state.connected} onChange={e => command("/settings/overlay", { shown: e.target.checked })} />
          <strong>Overlay</strong>
        </label>
      </div>
      <header className="topbar">
        <div><p className="eyebrow">SELECTED ROUTE</p><h1>{route?.name ?? "No route"}</h1><p className="statusLine"><span className={state.running ? "pulse" : "dot"} />{state.engineStatus}</p></div>
        <div className="transport">
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/run")}>▶ Run route</button>
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/duplicate")}>Duplicate</button>
          <button className="secondary" disabled={!route || route.favorite} onClick={() => { const name=window.prompt("Rename route",route?.name); if(name?.trim()) command("/routes/rename",{routeId:route?.id,name:name.trim()}); }}>Rename</button>
          <button className={`favoriteButton topFavorite ${route?.favorite?"active":""}`} disabled={!route} onClick={()=>command("/routes/favorite",{routeId:route?.id,favorite:!route?.favorite})}>{route?.favorite?"★ Locked":"☆ Favorite"}</button>
          <button className="record" disabled={!route || state.running} onClick={() => command(state.recording ? "/record/stop" : "/record/start")}>{state.recording ? "■ Stop recording" : "● Record"}</button>
          <button className="danger" disabled={!state.running && !state.recording} onClick={() => command("/routes/stop")}>Stop</button>
        </div>
      </header>

      <nav className="tabs">{[["overview","Overview"],["timeline","Timeline"],["recordings","Recordings"],["fulltas","Full-TAS"],["optimize","Optimize"],["checkpoints","Checkpoints"],["results","Results"]].map(([id,label]) => <button key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)}>{label}</button>)}</nav>
      {error && <div className="errorBanner">{error}<button onClick={() => setError("")}>×</button></div>}
      {state.paused && <div className="pauseBanner"><div><strong>The game is paused</strong><span>Automation needs the physics simulation running.</span></div><button onClick={() => command("/game/resume")}>Resume simulation</button></div>}

      <div className="content">
        {tab === "overview" && <div className="overviewGrid">
          <Stat label="Success rate" value={`${successRate.toFixed(1)}%`} detail={`${successes.length} of ${state.results.length} attempts`} tone="green" />
          <Stat label="Best time" value={bestResult ? `${bestResult.completionTime.toFixed(3)}s` : "—"} detail={bestResult ? `${bestResult.completionTicks} physics frames` : "Successful attempts"} tone="blue" />
          <Stat label="Route length" value={`${route?.actionCount ?? 0}`} detail="Semantic actions" />
          <Stat label="Start state" value={route?.checkpointId ? "Checkpoint" : "Quick restart"} detail={route?.checkpointId ? "Fingerprint protected" : "3 stable ticks"} />
        </div>}

        {tab === "timeline" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">FIXED-TICK SEQUENCE</p><h2>Route timeline</h2></div><span className="hint">{route?.favorite?"★ Favorited · read-only":"Changes save immediately"}</span></div><FrameTimeline actions={route?.actions ?? []}/><div className="timeline">{route?.actions.map((action, index) => <ActionCard key={action.id} action={action} index={index} locked={!!route?.favorite} onSave={(updates) => command("/actions/update", { actionIndex: index, ...updates })} />)}</div></div>}

        {tab === "recordings" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">RAW FIXED-FRAME CAPTURES</p><h2>Recordings</h2></div><span className="hint">{state.recording ? "Recording now…" : `${state.recordings.length} saved`}</span></div>{state.recording&&<p className="recordingActive">● Recording is active. Finishing the course saves it automatically.</p>}<div className="recordingList">{state.recordings.map(recording=><div className={`recordingCard ${recording.favorite?"locked":""}`} key={recording.id}><div className="recordingTitle"><div><strong>{recording.favorite?"★ ":""}{recording.name}</strong><small>{new Date(recording.createdUtc).toLocaleString()} · {recording.sampleCount} physics frames · {recording.actionCount} actions{recording.favorite?" · read-only":""}</small></div>{recording.courseTimeSeconds>0&&<b>{recording.courseTimeSeconds.toFixed(3)}s</b>}</div><p>{recording.actions.map(action=>`${action.type.toLowerCase()} ${DirectionText(action.direction)} ${action.durationTicks}f${action.type==="WALLJUMP"&&action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?` (hold ${DirectionText(action.wallJumpHoldDirection)})`:""}`).join(" → ")}</p><div className="buttonRow"><button className="primary" onClick={()=>command("/recordings/create-route",{recordingId:recording.id})}>Create route</button><button title="Name it Course1_To_Course2 or Course2_To_Bonus2 (optionally _WJ …): a transition route to optimize" onClick={()=>command("/recordings/create-transition",{recordingId:recording.id})}>Create transition</button><button disabled={recording.favorite} onClick={()=>{const name=window.prompt("Rename recording",recording.name);if(name?.trim())command("/recordings/rename",{recordingId:recording.id,name:name.trim()});}}>Rename</button><button className={`favoriteButton ${recording.favorite?"active":""}`} onClick={()=>command("/recordings/favorite",{recordingId:recording.id,favorite:!recording.favorite})}>{recording.favorite?"★ Locked":"☆ Favorite"}</button><button className="danger" disabled={recording.favorite} onClick={()=>{if(window.confirm(`Delete recording “${recording.name}”?`)) command("/recordings/delete",{recordingId:recording.id});}}>Delete</button></div></div>)}</div>{!state.recordings.length&&!state.recording&&<div className="emptyState"><span className="orbit">●</span><h2>No recordings yet</h2><p>Press Record, play the route, and finish the course.</p></div>}</div>}

        {tab === "fulltas" && <FullTas fullTas={state.fullTas} running={state.running} onCommand={command}/>}

        {tab === "optimize" && <div className="optimizeGrid"><Solver solver={state.solver ?? emptyState.solver} courses={state.courses ?? []} onCommand={command}/><SolveMatrix routes={state.routes} courses={state.courses ?? []} solver={state.solver ?? emptyState.solver} onCommand={command}/></div>}

        {tab === "checkpoints" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">START STATES</p><h2>Section checkpoints</h2></div><button className="primary" onClick={() => command("/checkpoints/capture")}>Capture current state</button></div><p className="lede">Assign a checkpoint to optimize an isolated section. Compatibility fingerprints prevent invalid restores.</p><div className="checkpointList">{state.checkpoints.map(cp => <div className={`checkpoint ${cp.assigned ? "assigned" : ""}`} key={cp.id}><div><strong>{cp.name}</strong><small>{new Date(cp.createdUtc).toLocaleString()}</small></div>{cp.assigned && <span className="badge">Route start</span>}<button onClick={() => command("/checkpoints/restore", { checkpointId: cp.id })}>Restore</button><button className="secondary" onClick={() => command("/checkpoints/assign", { checkpointId: cp.assigned ? "" : cp.id })}>{cp.assigned ? "Unassign" : "Use as start"}</button></div>)}</div></div>}

        {tab === "results" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">ATTEMPT HISTORY</p><h2>Results</h2></div><span className="hint">{state.results.length} attempts</span></div><div className="resultTable"><div className="tableHead"><span>Outcome</span><span>Time</span><span>Frames</span><span>When</span></div>{state.results.map(r => <div className="tableRow" key={r.id}><span className={r.success ? "success" : "failure"}>{r.success ? "✓ Success" : "× Failed"}{r.deterministicPhysics ? " · canonical" : ""}</span><span title={r.timingSource}>{r.success ? `${r.completionTime.toFixed(3)}s` : r.failureReason}</span><span>{r.completionTicks}</span><span>{new Date(r.timestampUtc).toLocaleString()}</span></div>)}</div></div>}
      </div>
    </section>
  </main>;
}

const CATEGORIES: [string, string][] = [["NO_WJ", "No wall jump"], ["WJ", "Wall jump"], ["WJDASH", "Wall jump + dash"], ["WJ_DASH_AJ", "Wall jump + dash + air jump"]];
const CATEGORY_ROUTE = /^([^_\s]+)_(NO_WJ|WJDASH|WJ_DASH_AJ|WJ) ([\d.]+)$/i;

const place = (p: string) => /^\d+$/.test(p) ? `Course ${p}` : p.replace(/^bonus/i, "Bonus ");

function courseLabel(c: Course | undefined, id: number) {
  if (!c) return `Course ${id}`;
  return c.to ? `${place(c.from ?? "")} → ${place(c.to)}` : place(c.token);
}

// The widest category a course has: a bonus only has its first one (the unlocks it needs), everything else runs in every wider one too.
function topLevel(c: Course) { return c.bonus ? c.minLevel ?? 0 : CATEGORIES.length - 1; }

// The fastest category route per "<course id>:<CAPS>"; slower ones and routes that aren't a known course's category
// routes go to `other`.
function categoryBests(routes: Route[], courses: Course[]) {
  const best = new Map<string, {route: Route; time: number}>();
  const other: Route[] = [];
  for (const r of routes) {
    const m = CATEGORY_ROUTE.exec(r.name);
    const course = m && courses.find(c => c.token.toLowerCase() === m[1].toLowerCase());
    const level = m ? CATEGORIES.findIndex(([cat]) => cat === m[2].toUpperCase()) : -1;
    if (!m || !course || level < (course.minLevel ?? 0) || level > topLevel(course)) { other.push(r); continue; }
    const key = `${course.id}:${m[2].toUpperCase()}`, time = Number(m[3]);
    const current = best.get(key);
    if (!current || time < current.time) best.set(key, {route: r, time});
    else other.push(r);
  }
  return {best, other};
}

const SHORT_CATEGORY: Record<string, string> = { NO_WJ: "No WJ", WJ: "WJ", WJDASH: "WJ + Dash", WJ_DASH_AJ: "WJ + Dash + AJ" };

// Every course, transition and bonus by category with its fastest solo route (from its own start). A solo route runs
// in every wider category, so a category's time is the fastest over it and the narrower ones. A category wider than
// `unlockLevel` (the save's unlocks) is dimmed: your save can't run it by hand.
function RouteSidebar({routes,courses,activeId,unlockLevel,onCommand}:{routes:Route[];courses:Course[];activeId?:string;unlockLevel:number|null;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const {best, other} = categoryBests(routes, courses);
  const cell = (routeId: string | undefined, text: string, title: string) => routeId
    ? <button className={`cmpCell ${routeId === activeId ? "active" : ""}`} title={title} onClick={() => onCommand("/routes/select", { routeId })}>{text}</button>
    : <span className="cmpCell empty" title={title}>{text}</span>;
  const rows = (c: Course) => CATEGORIES.slice(c.minLevel ?? 0, topLevel(c) + 1).flatMap(([cat], i) => {
    let solo: {route: Route; time: number} | undefined;
    for (const [narrower] of CATEGORIES.slice(c.minLevel ?? 0, (c.minLevel ?? 0) + i + 1)) {
      const b = best.get(`${c.id}:${narrower}`);
      if (b && (!solo || b.time < solo.time)) solo = b;
    }
    // A category with no route of its own would only repeat a narrower one's row.
    if (!best.has(`${c.id}:${cat}`)) return [];
    const beyond = unlockLevel != null && (c.minLevel ?? 0) + i > unlockLevel;
    return [<div className={`cmpRow ${beyond ? "beyondUnlocks" : ""}`} key={cat} title={beyond ? `Your save's unlocks only reach ${CATEGORIES[unlockLevel][1]}` : undefined}>
      <span>{SHORT_CATEGORY[cat]}</span>
      {cell(solo?.route.id, solo ? solo.time.toFixed(2) : "—", solo?.route.name ?? "No solo route")}
    </div>];
  });
  const section = (label: string, list: Course[]) => {
    const groups = list.map(c => ({c, r: rows(c)})).filter(g => g.r.length > 0);
    return groups.length > 0 && <>
      <div className="cmpHead"><label className="sectionLabel">{label}</label><span>Solo</span></div>
      {groups.map(({c, r}) => <div className="courseGroup" key={c.token}><strong className="courseName">{courseLabel(c, c.id)}</strong>{r}</div>)}
    </>;
  };
  return <div className="courseList">
    {section("COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id))}
    {section("TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id))}
    {section("BONUSES", courses.filter(c => c.bonus && !c.to).sort((a, b) => a.id - b.id))}
    {other.length > 0 && <details className="otherRoutes"><summary className="sectionLabel">OTHER ROUTES ({other.length})</summary>
      <div className="routeList">{other.map(item => <div className={`routeRow ${item.id === activeId ? "active" : ""}`} key={item.id} onContextMenu={(event) => { event.preventDefault(); if (!item.favorite && window.confirm(`Delete route “${item.name}”?`)) onCommand("/routes/delete", { routeId: item.id }); }}><button className="routeItem" onClick={() => onCommand("/routes/select", { routeId: item.id })}><div><strong>{item.favorite ? "★ " : ""}{item.name}</strong><small>{item.actionCount} actions{item.favorite ? " · read-only" : item.hotkey ? ` · ${item.hotkey}` : ""}</small></div></button><button className={`favoriteButton ${item.favorite?"active":""}`} title={item.favorite?"Unfavorite and unlock":"Favorite and lock"} onClick={()=>onCommand("/routes/favorite",{routeId:item.id,favorite:!item.favorite})}>{item.favorite?"★":"☆"}</button></div>)}</div>
    </details>}
  </div>;
}
function DirectionText(direction:number) { return direction < 0 ? "←" : direction > 0 ? "→" : "neutral"; }
function Stat({label,value,detail,tone=""}:{label:string,value:string,detail:string,tone?:string}) { return <div className={`stat ${tone}`}><p>{label}</p><strong>{value}</strong><span>{detail}</span></div>; }
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
function ActionCard({action,index,locked,onSave}:{action:Action;index:number;locked:boolean;onSave:(u:Record<string,unknown>)=>void}) { const [duration,setDuration]=useState(action.durationTicks); useEffect(()=>setDuration(action.durationTicks),[action.durationTicks]); const hold=action.type==="WALLJUMP"?` · post-launch ${action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?DirectionText(action.wallJumpHoldDirection):"auto"}`:""; return <div className={`actionCard ${!action.enabled ? "disabled" : ""} ${locked?"locked":""}`}><b>{index+1}</b><div className="actionMain"><strong>{action.label}</strong><small>Physics frames{hold}</small></div><select disabled={locked} value={action.type} onChange={e=>onSave({type:e.target.value})}>{["RAWINPUT","MOVE","JUMP","DASH","WAIT","WALLJUMP"].map(type=><option key={type} value={type}>{type==="RAWINPUT"?"EXACT INPUT":type}</option>)}</select><div className="direction">{action.direction < 0 ? "←" : action.direction > 0 ? "→" : "•"}</div><div className="inlineInput"><input disabled={locked} type="number" min="1" value={duration} onChange={e=>setDuration(Number(e.target.value))}/><span>f</span></div><button disabled={locked} onClick={()=>onSave({durationTicks:duration})}>Save</button><button disabled={locked} className="secondary" onClick={()=>onSave({enabled:!action.enabled})}>{action.enabled ? "Disable" : "Enable"}</button></div>; }
function Solver({solver,courses,onCommand}:{solver:SolverState;courses:Course[];onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const [course,setCourse]=useState<number|"">("");
  const [caps,setCaps]=useState("wj");
  const chosen=course===""?(solver.courses[0]??1):course;
  // A course has no categories below its minimum (Course 2 starts at WJ, 3 at WJDASH, 4 at WJ_DASH_AJ); a bonus only its first.
  const chosenCourse=courses.find(c=>c.id===chosen);
  const minLevel=chosenCourse?.minLevel??0;
  const capsOptions=[["none","Nothing"],["wj","Wall jump"],["wjdash","Wall jump + dash"],["wjdashaj","Wall jump + dash + air jump"]].slice(minLevel,chosenCourse?topLevel(chosenCourse)+1:undefined);
  const chosenCaps=capsOptions.some(([v])=>v===caps)?caps:capsOptions[0][0];
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">OFFLINE SOLVER</p><h2>Solve a course</h2></div><span className={`offlineStatus ${solver.running?"running":""}`}>{solver.running?"Solving":"Idle"}</span></div>
    <p className="lede">Captures the course start in the game, then searches offline on every core: it explores new paths from the start and sharpens the best finish; only the start and stop timer gates matter. Every offline best is replayed in the game (turbo) and, if it holds up there, saved as a route that replaces slower ones.</p>
    <div className="offlineControls">
      <Field label="1. Course"><select value={chosen} disabled={solver.running} onChange={e=>{ if(e.target.value==="new") { let id=1; while(solver.courses.includes(id)) id++; onCommand("/courses/add-from-recording").then(()=>setCourse(id)); } else setCourse(Number(e.target.value)); }}>{solver.courses.map(id=><option key={id} value={id}>{courseLabel(courses.find(c=>c.id===id),id)}</option>)}<option value="new">+ New course from last recording</option></select></Field>
      <Field label="2. Capabilities"><select value={chosenCaps} disabled={solver.running} onChange={e=>setCaps(e.target.value)}>{capsOptions.map(([v,label])=><option key={v} value={v}>{label}</option>)}</select></Field>
    </div>
    <div className="buttonRow">{solver.running?<button className="danger" onClick={()=>onCommand("/solver/stop")}>Stop</button>:<button className="primary" onClick={()=>onCommand("/solver/start",{course:chosen,capabilities:chosenCaps})}>3. Solve</button>}</div>
    <div className="offlineSummary"><div><span>Best</span><strong>{solver.best!=null?`${solver.best.toFixed(2)}s`:"—"}</strong><small>{solver.course?`${courseLabel(courses.find(c=>c.id===solver.course),solver.course)} · ${solver.capabilities}`:"Not started"}</small></div><div><span>Runs</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.finishes.toLocaleString()} timed finishes</small></div><div><span>Turbo checks</span><strong>{solver.turboChecks.toLocaleString()}</strong><small>{solver.turboSaved.toLocaleString()} saved as routes</small></div><div><span>To validate</span><strong>{(solver.toValidate??0).toLocaleString()}</strong><small>offline bests waiting for a turbo check</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.log.length>0&&<ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul>}
  </div>;
}

// The solver's capability values, indexed like CATEGORIES.
const CAPS = ["none", "wj", "wjdash", "wjdashaj"];
type SolveTarget = { key: string; body: { course: number; capabilities: string }; time?: number };

// How Optimize many spends its time (kept in the browser): `minutes` per target, and with `repeat` the queue starts
// over when it ends, each pass with twice the minutes up to `maxMinutes`, until stopped. Short first passes get every
// target a route fast; later ones dig deeper (leave it overnight).
type Passes = { minutes: number; maxMinutes: number; repeat: boolean };
function usePasses(): [Passes, (update: Partial<Passes>) => void] {
  const [passes, setPasses] = useState<Passes>({ minutes: 1, maxMinutes: 15, repeat: true });
  useEffect(() => {
    try { const saved = JSON.parse(window.localStorage.getItem("optimizePasses") ?? "null"); if (saved) setPasses(p => ({ ...p, ...saved })); }
    catch { /* a corrupt value falls back to the defaults */ }
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

// "Pass 3 · target 4 of 55 · 2 min each": where an optimize run is.
function progressText(solver: SolverState) {
  const queue = solver.queue ?? [], at = queue.findIndex(q => q.active);
  return [solver.repeat ? `Pass ${solver.pass ?? 1}` : "", at >= 0 && queue.length > 1 ? `target ${at + 1} of ${queue.length}` : "", solver.minutes ? `${minutesText(solver.minutes)} each` : ""].filter(Boolean).join(" · ");
}

// Batch optimize: every course's categories as a checkbox, solved one after another in the matrix's order (top to
// bottom, left to right). The selection and passes are kept in the browser.
function SolveMatrix({routes,courses,solver,onCommand}:{routes:Route[];courses:Course[];solver:SolverState;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [passes, updatePasses] = usePasses();
  useEffect(() => setSelected(new Set(JSON.parse(window.localStorage.getItem("optimizeSelection") ?? "[]"))), []);
  const select = (next: Set<string>) => { setSelected(next); window.localStorage.setItem("optimizeSelection", JSON.stringify([...next])); };
  // Ticks every key, unless all of them already are: then clears them.
  const toggle = (keys: string[]) => {
    const next = new Set(selected), on = keys.some(k => !next.has(k));
    for (const k of keys) { if (on) next.add(k); else next.delete(k); }
    select(next);
  };
  const {best} = categoryBests(routes, courses);
  const groups: [string, Course[]][] = [
    ["COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id)],
    ["TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id)],
    ["BONUSES", courses.filter(c => c.bonus).sort((a, b) => a.id - b.id)]];
  const cells = (c: Course): (SolveTarget | null)[] => CATEGORIES.map(([cat], level) => level < (c.minLevel ?? 0) || level > topLevel(c) ? null
    : { key: `c:${c.id}:${CAPS[level]}`, body: { course: c.id, capabilities: CAPS[level] }, time: best.get(`${c.id}:${cat}`)?.time });
  const all = groups.flatMap(([, list]) => list.flatMap(c => cells(c).filter((t): t is SolveTarget => !!t)));
  const queue = all.filter(t => selected.has(t.key));
  const checkbox = (t: SolveTarget) => <label className={`matrixCell ${selected.has(t.key) ? "on" : ""}`} title={t.time != null ? `Current best ${t.time.toFixed(2)}s` : "No route yet"}>
    <input type="checkbox" checked={selected.has(t.key)} onChange={() => toggle([t.key])} /><span>{t.time != null ? `${t.time.toFixed(2)}s` : "—"}</span></label>;
  const progress = solver.queue ?? [];
  return <div className="panel offlinePanel solveMatrix"><div className="panelTitle"><div><p className="eyebrow">BATCH SOLVER</p><h2>Optimize many</h2></div><span className={`offlineStatus ${solver.running ? "running" : ""}`}>{solver.running ? progressText(solver) || "Solving" : "Idle"}</span></div>
    <p className="lede">Tick what to optimize (a row or column header ticks all of it). Each target is solved in turn exactly like Solve, for up to its minutes or until half of them (at most 5) pass without a new best; every gain goes through the same turbo check and gate. Times are the current best.</p>
    <div className="matrixTools">
      <button className="secondary" onClick={() => select(new Set(all.map(t => t.key)))}>All</button>
      <button className="secondary" onClick={() => select(new Set(all.filter(t => t.time != null).map(t => t.key)))} title="Only targets that already have a route">With a time</button>
      <button className="secondary" onClick={() => select(new Set())}>None</button>
    </div>
    <PassFields passes={passes} update={updatePasses} disabled={solver.running} />
    <div className="matrixScroll"><table className="matrix">
      <thead><tr><th />{CATEGORIES.map(([cat, title], level) => <th key={cat}><button title={`Tick every ${title.toLowerCase()} target`} onClick={() => toggle(groups.flatMap(([, list]) => list.map(c => cells(c)[level]).filter((t): t is SolveTarget => !!t).map(t => t.key)))}>{cat}</button></th>)}</tr></thead>
      <tbody>
        {groups.map(([label, list]) => list.length > 0 && <Fragment key={label}>
          <tr className="matrixSection"><th colSpan={5}>{label}</th></tr>
          {list.map(c => <tr key={c.id}>
            <th><button onClick={() => toggle(cells(c).filter((t): t is SolveTarget => !!t).map(t => t.key))}>{courseLabel(c, c.id)}</button></th>
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

// "Finished · 812.34s · saved" / "Not finished · <reason>": a gate reply in one line.
function replyText(r: FullTasReply | string | null | undefined) {
  if (r == null) return "";
  if (typeof r === "string") return r;
  const parts = [r.finished === undefined ? "" : r.finished ? "Finished" : "Not finished", r.time != null ? `${r.time.toFixed(2)}s` : "", r.saved === undefined ? "" : r.saved ? "saved" : "not saved", r.reason ?? "", r.trace ? `trace ${r.trace}` : ""];
  return parts.filter(p => p).join(" · ");
}

// Full-TAS: the whole game from a fresh new game as one continuous input stream, scored from its first tick to its last
// goal. The goals split it; the offline search (Solve) works through it window by window and the game's gate saves a
// route only when it is faster.
function FullTas({fullTas,running,onCommand}:{fullTas?:FullTasState;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const runs = fullTas?.runs ?? [];
  const [selected, setSelected] = useState("");
  const [newName, setNewName] = useState("");
  const [minutes, setMinutes] = useState(60);
  const [windowMinutes, setWindowMinutes] = useState(1);
  const [window_, setWindow] = useState(2);
  const name = selected || runs[0]?.name || "";
  const run = runs.find(r => r.name === name);
  const savedGoals = (run?.tokens ?? run?.goals ?? []).join("\n");
  const [draft, setDraft] = useState(savedGoals);
  // The editor shows the selected run's saved goals again whenever the run or its saved goals change.
  useEffect(() => setDraft(savedGoals), [name, savedGoals]);
  if (!fullTas) return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className="offlineStatus">Unavailable</span></div>
    <p className="lede">The game plugin doesn&apos;t report Full-TAS runs (an older version, or not connected).</p></div>;
  const solve: FullTasState["solve"] = fullTas.solve ?? { running: false, status: "", log: [] };
  const busy = solve.running || running;
  const goals = draft.split("\n").map(g => g.trim()).filter(g => g);
  const validName = /^[A-Za-z0-9]+$/.test(newName);
  const splits = run?.best?.splits ?? run?.offline?.splits ?? [];
  const labels = run ? (splits.length > run.goals.length ? splits.map(s => s.label) : run.goals) : [];
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE WHOLE GAME AS ONE RUN</p><h2>Full-TAS</h2></div><span className={`offlineStatus ${busy?"running":""}`}>{solve.running?"Solving":running?"Playing":"Idle"}</span></div>
    <p className="lede">A Full-TAS run is the whole game from a fresh new game as one continuous input stream, timed from its first tick to its last goal. Goals (one token per line: <code>start:1</code>, <code>finish:1</code>, <code>buy:buyreward</code>, <code>reward:bonus1</code>) are reached in order; they only split the run, nothing restarts between them (a quick restart is an input the route chooses). <b>Solve</b> searches offline window by window; the game replays every better route from a new game and saves it only when it holds up and is faster.</p>
    <div className="fullTasRuns">
      {runs.map(r => <button key={r.name} className={`fullTasRun ${r.name===name?"selected":""}`} onClick={() => setSelected(r.name)}>
        <strong>{r.name}</strong><small>{r.goals.length} goal{r.goals.length===1?"":"s"}</small>
        {r.best?<b>{r.best.time.toFixed(2)}s</b>:r.offline?<b className="none">offline {r.offline.reached}/{r.goals.length} · {r.offline.time.toFixed(2)}s</b>:<b className="none">no route yet</b>}
        <span className={`badge ${r.traceReady?"":"missing"}`}>{r.traceReady?"Trace ready":"No trace"}</span>
      </button>)}
      {name && !run && <div className="fullTasRun selected unsaved"><strong>{name}</strong><small>New · save its goals to create it</small></div>}
    </div>
    {!name && <p className="offlineConfirmation">No Full-TAS run yet: name a new one.</p>}
    <div className="fullTasInline">
      <Field label="New run (letters and digits)"><input value={newName} placeholder="main" onChange={e => setNewName(e.target.value)}/></Field>
      <button className="secondary" disabled={!validName || runs.some(r => r.name === newName)} onClick={() => { setSelected(newName); setNewName(""); }}>+ New run</button>
    </div>
    {name && <div className="fullTasGrid">
      <div className="fullTasGoals">
        <div className="panelTitle"><div><p className="eyebrow">{run?.best?run.best.name:run?.offline?`OFFLINE BEST · ${run.offline.reached} OF ${run.goals.length} GOALS · NOT SAVED (ONLY COMPLETE RUNS GO TO THE GATE)`:"NO ROUTE YET"}</p><h2>{name}{run?.best?` · ${run.best.time.toFixed(2)}s`:run?.offline?` · ${run.offline.time.toFixed(2)}s to goal ${run.offline.reached}`:""}</h2></div></div>
        <div className="resultTable">
          <div className="tableHead"><span>#</span><span>Goal</span><span>Split</span><span>Cumulative</span></div>
          {labels.map((label, i) => { const s = splits[i]; return <div className={`tableRow ${s?"":"pending"}`} key={i}>
            <span>{i + 1}</span><span>{s?.label ?? label}</span>
            <span>{s ? tickText(s.ticks - (i > 0 ? splits[i - 1].ticks : 0)) : "—"}</span>
            <span>{s ? tickText(s.ticks) : "—"}</span>
          </div>; })}
          {!labels.length && <div className="tableRow pending"><span>—</span><span>No goals</span></div>}
        </div>
        <div className="fullTasSolve">
          <div className="buttonRow">
            <button className="secondary" disabled={busy || !run} title="Starts a trace and world capture from a fresh new game (seeded with the saved best's inputs)" onClick={() => onCommand("/fulltas/capture", { name })}>Capture start</button>
            <button className="secondary" disabled={busy || !(run?.playable ?? run?.best)} title={run?.best ? "Plays the saved best in real time from a new game" : "Plays the offline search's best so far in real time from a new game (it stops at the last goal it reaches)"} onClick={() => onCommand("/fulltas/play", { name })}>▶ Play</button>
            <button className="danger" disabled={!busy} onClick={() => onCommand("/fulltas/stop")}>Stop</button>
          </div>
          <div className="offlineControls">
            <Field label="Minutes (total)"><input type="number" min="1" value={minutes} disabled={solve.running} onChange={e => setMinutes(Number(e.target.value))}/></Field>
            <Field label="Minutes per window"><input type="number" min="0.1" step="0.1" value={windowMinutes} disabled={solve.running} onChange={e => setWindowMinutes(Number(e.target.value))}/></Field>
            <Field label="Window (goals)"><input type="number" min="1" value={window_} disabled={solve.running} onChange={e => setWindow(Number(e.target.value))}/></Field>
          </div>
          <div className="buttonRow"><button className="primary" disabled={busy || !run || !run.goals.length} title={run?.traceReady ? "Searches offline from the captured trace" : "Captures the start first (no trace yet), then searches offline"} onClick={() => onCommand("/fulltas/solve", { name, minutes, windowMinutes, window: window_ })}>⟳ Solve</button></div>
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
      <p className="offlineConfirmation">{solve.status || "Idle"}</p>
      {fullTas.lastResult != null && <p className="offlineConfirmation">Last result: {replyText(fullTas.lastResult)}</p>}
      {(solve.log?.length ?? 0) > 0 && <ul className="solverLog">{solve.log.map((line, i) => <li key={i}>{line}</li>)}</ul>}
    </div>
  </div>;
}
