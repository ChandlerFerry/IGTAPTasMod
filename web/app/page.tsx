"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import "./action-editor.css";
import "./frame-timeline.css";
import "./favorite-ui.css";
import "./route-sidebar.css";

const API = "http://127.0.0.1:17426/api";

type Action = { id: string; label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number; enabled: boolean; rawMoveX?: number; rawMoveY?: number; rawJumpPressed?: boolean; rawJumpHeld?: boolean; rawJumpReleased?: boolean; rawDashPressed?: boolean; rawDashHeld?: boolean };
type Route = { id: string; name: string; favorite: boolean; hotkey: string; checkpointId: string; actions: Action[] };
type RecordedAction = { label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number };
type Recording = { id: string; name: string; favorite: boolean; createdUtc: string; sampleCount: number; actionCount: number; courseTimeSeconds: number; actions: RecordedAction[] };
type Checkpoint = { id: string; name: string; createdUtc: string; assigned: boolean };
type Result = { id: string; success: boolean; completionTime: number; completionTicks: number; timingSource?: string; deterministicPhysics?: boolean; failureReason?: string; timestampUtc: string };
// A bonus has a reward box instead of gates; a transition runs between two places: a course ("2") or a bonus ("bonus2").
type Course = { id: number; token: string; bonus: boolean; minLevel: number; from?: string; to?: string };
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; chain?: string | null; segment?: number; evaluations: number; finishes: number; turboChecks: number; turboSaved: number; best: number | null; log: string[]; courses: number[] };
// A chain: the game in order after an unlock, each segment starting where the one before ended ("restart" = the game's quick restart first).
type ChainSegment = { index: number; token: string; restart: boolean; bonus: boolean; from?: string; to?: string; routeId?: string | null; time?: number | null; staleRoutes: number };
type Chain = { name: string; capabilities: string; error?: string | null; segments: ChainSegment[]; total?: number | null };
type ChainRun = { running: { chain: string; segment: number; of: number; token: string; splits: number[] } | null; last?: string | null };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean; overlayShown: boolean;
  // The widest category the save's own unlocks can run (0 NO_WJ … 3 WJ_DASH_AJ); null when the player isn't loaded.
  unlockLevel?: number | null;
  selectedRouteId: string; routes: Route[]; courses?: Course[]; chains?: Chain[]; chainRun?: ChainRun; recordings: Recording[]; checkpoints: Checkpoint[]; results: Result[]; solver: SolverState;
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
  // View preference only (kept in the browser): list just the routes the save's unlocks can execute.
  const [executableOnly, setExecutableOnly] = useState(false);
  useEffect(() => setExecutableOnly(window.localStorage.getItem("executableOnly") === "1"), []);
  const toggleExecutableOnly = (on: boolean) => { setExecutableOnly(on); window.localStorage.setItem("executableOnly", on ? "1" : "0"); };
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
  // The chain the selected route is a segment of (its "Run route" plays the chain only through that segment).
  const routeChain = route ? (state.chains ?? []).find(c => c.segments.some(s => s.routeId === route.id)) : undefined;
  const successes = state.results.filter(r => r.success);
  const successRate = state.results.length ? successes.length / state.results.length * 100 : 0;
  const bestResult = successes.length ? successes.reduce((best,current)=>current.completionTime<best.completionTime?current:best) : null;

  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brandMark">T</span><div><strong>IGTAP</strong><small>TAS STUDIO</small></div></div>
      <div className={`connection ${state.connected ? "online" : "offline"}`}><i />{state.connected ? `Connected · v${state.version}` : "Waiting for game"}</div>
      <RouteSidebar routes={state.routes} courses={state.courses ?? []} chains={state.chains ?? []} activeId={route?.id} maxLevel={executableOnly ? unlockLevel : null} running={state.running} onCommand={command} />
      <div className="sidebarFooter"><p>The game keeps simulating while this window is focused.</p><kbd>F8</kbd><span>Emergency stop</span></div>
    </aside>

    <section className="workspace">
      <label className={`capabilityLock ${state.capabilitiesLocked ? "on" : ""}`}>
        <input type="checkbox" checked={state.capabilitiesLocked} disabled={!state.connected} onChange={e => command("/settings/lock-capabilities", { locked: e.target.checked })} />
        <strong>DO NOT MODIFY CAPABILITIES</strong>
        <span>{state.capabilitiesLocked ? "On: the plugin never changes your unlocks. The solver and turbo tools are disabled." : "Off: category routes and the solver set wall jump / dash for their category."}</span>
      </label>
      <label className={`capabilityLock executable ${executableOnly ? "on" : ""}`}>
        <input type="checkbox" checked={executableOnly} onChange={e => toggleExecutableOnly(e.target.checked)} />
        <strong>ONLY ROUTES I CAN RUN</strong>
        <span>{!executableOnly ? "Off: the sidebar lists every category's fastest route." : unlockLevel == null ? "On, but your unlocks are unknown until the game is in the world." : `On: each course shows its fastest route with your unlocks (${CATEGORIES[unlockLevel][1]}).`}</span>
      </label>
      <label className={`capabilityLock executable ${state.overlayShown ? "on" : ""}`}>
        <input type="checkbox" checked={state.overlayShown} disabled={!state.connected} onChange={e => command("/settings/overlay", { shown: e.target.checked })} />
        <strong>SHOW TAS OVERLAY</strong>
        <span>{state.overlayShown ? "On: the game shows the player's hitbox and, on the left, the inputs playback presses." : "Off: the game draws nothing of the studio's."}</span>
      </label>
      <header className="topbar">
        <div><p className="eyebrow">SELECTED ROUTE</p><h1>{route?.name ?? "No route"}</h1><p className="statusLine"><span className={state.running ? "pulse" : "dot"} />{state.engineStatus}</p></div>
        <div className="transport">
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/run")}>▶ Run route</button>
          {routeChain && <button className="primary" disabled={state.running || !chainPlayable(routeChain)} title={chainPlayable(routeChain) ? `Play all ${routeChain.segments.length} segments of ${routeChain.name}` : "Every segment needs a route first"} onClick={() => command("/chains/run", { chain: routeChain.name })}>▶ Run whole chain</button>}
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/duplicate")}>Duplicate</button>
          <button className="secondary" disabled={!route || route.favorite} onClick={() => { const name=window.prompt("Rename route",route?.name); if(name?.trim()) command("/routes/rename",{routeId:route?.id,name:name.trim()}); }}>Rename</button>
          <button className={`favoriteButton topFavorite ${route?.favorite?"active":""}`} disabled={!route} onClick={()=>command("/routes/favorite",{routeId:route?.id,favorite:!route?.favorite})}>{route?.favorite?"★ Locked":"☆ Favorite"}</button>
          <button className="record" disabled={!route || state.running} onClick={() => command(state.recording ? "/record/stop" : "/record/start")}>{state.recording ? "■ Stop recording" : "● Record"}</button>
          <button className="danger" disabled={!state.running && !state.recording} onClick={() => command("/routes/stop")}>Stop</button>
        </div>
      </header>

      <nav className="tabs">{[["overview","Overview"],["timeline","Timeline"],["recordings","Recordings"],["chains","Chains"],["optimize","Optimize"],["checkpoints","Checkpoints"],["results","Results"]].map(([id,label]) => <button key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)}>{label}</button>)}</nav>
      {error && <div className="errorBanner">{error}<button onClick={() => setError("")}>×</button></div>}
      {state.paused && <div className="pauseBanner"><div><strong>The game is paused</strong><span>Automation needs the physics simulation running.</span></div><button onClick={() => command("/game/resume")}>Resume simulation</button></div>}

      <div className="content">
        {tab === "overview" && <div className="overviewGrid">
          <Stat label="Success rate" value={`${successRate.toFixed(1)}%`} detail={`${successes.length} of ${state.results.length} attempts`} tone="green" />
          <Stat label="Best time" value={bestResult ? `${bestResult.completionTime.toFixed(3)}s` : "—"} detail={bestResult ? `${bestResult.completionTicks} physics frames` : "Successful attempts"} tone="blue" />
          <Stat label="Route length" value={`${route?.actions.length ?? 0}`} detail="Semantic actions" />
          <Stat label="Start state" value={route?.checkpointId ? "Checkpoint" : "Quick restart"} detail={route?.checkpointId ? "Fingerprint protected" : "3 stable ticks"} />
        </div>}

        {tab === "timeline" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">FIXED-TICK SEQUENCE</p><h2>Route timeline</h2></div><span className="hint">{route?.favorite?"★ Favorited · read-only":"Changes save immediately"}</span></div><FrameTimeline actions={route?.actions ?? []}/><div className="timeline">{route?.actions.map((action, index) => <ActionCard key={action.id} action={action} index={index} locked={!!route?.favorite} onSave={(updates) => command("/actions/update", { actionIndex: index, ...updates })} />)}</div></div>}

        {tab === "recordings" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">RAW FIXED-FRAME CAPTURES</p><h2>Recordings</h2></div><span className="hint">{state.recording ? "Recording now…" : `${state.recordings.length} saved`}</span></div>{state.recording&&<p className="recordingActive">● Recording is active. Finishing the course saves it automatically.</p>}<div className="recordingList">{state.recordings.map(recording=><div className={`recordingCard ${recording.favorite?"locked":""}`} key={recording.id}><div className="recordingTitle"><div><strong>{recording.favorite?"★ ":""}{recording.name}</strong><small>{new Date(recording.createdUtc).toLocaleString()} · {recording.sampleCount} physics frames · {recording.actionCount} actions{recording.favorite?" · read-only":""}</small></div>{recording.courseTimeSeconds>0&&<b>{recording.courseTimeSeconds.toFixed(3)}s</b>}</div><p>{recording.actions.map(action=>`${action.type.toLowerCase()} ${DirectionText(action.direction)} ${action.durationTicks}f${action.type==="WALLJUMP"&&action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?` (hold ${DirectionText(action.wallJumpHoldDirection)})`:""}`).join(" → ")}</p><div className="buttonRow"><button className="primary" onClick={()=>command("/recordings/create-route",{recordingId:recording.id})}>Create route</button><button title="Name it Course1_To_Course2 or Course2_To_Bonus2 (optionally _WJ …): a transition route to optimize" onClick={()=>command("/recordings/create-transition",{recordingId:recording.id})}>Create transition</button><button disabled={recording.favorite} onClick={()=>{const name=window.prompt("Rename recording",recording.name);if(name?.trim())command("/recordings/rename",{recordingId:recording.id,name:name.trim()});}}>Rename</button><button className={`favoriteButton ${recording.favorite?"active":""}`} onClick={()=>command("/recordings/favorite",{recordingId:recording.id,favorite:!recording.favorite})}>{recording.favorite?"★ Locked":"☆ Favorite"}</button><button className="danger" disabled={recording.favorite} onClick={()=>{if(window.confirm(`Delete recording “${recording.name}”?`)) command("/recordings/delete",{recordingId:recording.id});}}>Delete</button></div></div>)}</div>{!state.recordings.length&&!state.recording&&<div className="emptyState"><span className="orbit">●</span><h2>No recordings yet</h2><p>Press Record, play the route, and finish the course.</p></div>}</div>}

        {tab === "chains" && <Chains chains={state.chains ?? []} chainRun={state.chainRun} solver={state.solver ?? emptyState.solver} running={state.running} onCommand={command}/>}

        {tab === "optimize" && <Solver solver={state.solver ?? emptyState.solver} courses={state.courses ?? []} onCommand={command}/>} 

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
  return c.to ? `${place(c.from ?? "")} → ${place(c.to)}` : c.bonus ? c.token : `Course ${c.token}`;
}

function segmentLabel(s: ChainSegment) { return s.to ? `${place(s.from ?? "")} → ${place(s.to)}` : place(s.token); }

// A chain plays end to end only when every segment has a route.
function chainPlayable(c: Chain) { return !c.error && c.segments.every(s => !!s.routeId); }

// The widest category a course has: a bonus only has its first one (the unlocks it needs), everything else runs in every wider one too.
function topLevel(c: Course) { return c.bonus ? c.minLevel ?? 0 : CATEGORIES.length - 1; }

// Courses in order, then transitions, bonuses last; each shows its fastest route per category, or with `maxLevel` (the
// save's unlocks) only its fastest route those unlocks can run. Everything else is under "Other routes".
function RouteSidebar({routes,courses,chains,activeId,maxLevel,running,onCommand}:{routes:Route[];courses:Course[];chains:Chain[];activeId?:string;maxLevel:number|null;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const best = new Map<string, {route: Route; time: number}>();
  const other: Route[] = [];
  // A chain's routes are listed under their chain; its stale and slower ones stay under "Other routes".
  const chainRoutes = new Set(chains.flatMap(c => c.segments.map(s => s.routeId).filter((id): id is string => !!id)));
  for (const r of routes) {
    if (chainRoutes.has(r.id)) continue;
    const m = CATEGORY_ROUTE.exec(r.name);
    const course = m && courses.find(c => c.token.toLowerCase() === m[1].toLowerCase());
    const level = m ? CATEGORIES.findIndex(([cat]) => cat === m[2].toUpperCase()) : -1;
    if (!m || !course || level < (course.minLevel ?? 0) || level > topLevel(course)) { other.push(r); continue; }
    const key = `${course.id}:${m[2].toUpperCase()}`, time = Number(m[3]);
    const current = best.get(key);
    if (!current || time < current.time) best.set(key, {route: r, time});
    else other.push(r);
  }
  const row = (key: string, title: string, b?: {route: Route; time: number}) => b
    ? <button key={key} className={`categoryRow ${b.route.id === activeId ? "active" : ""}`} onClick={() => onCommand("/routes/select", { routeId: b.route.id })} title={b.route.name}><span>{title}</span><b>{b.time.toFixed(2)}s</b></button>
    : <div key={key} className="categoryRow empty"><span>{title}</span><b>—</b></div>;
  // A route is valid in every wider category, so the fastest one the unlocks can run is the best over the categories up to them.
  const executable = (c: Course) => {
    const min = c.minLevel ?? 0;
    if (maxLevel == null) return null;
    if (maxLevel < min) return <div key="needs" className="categoryRow empty"><span>Needs {CATEGORIES[min][1].toLowerCase()}</span><b>—</b></div>;
    let pick = min;
    for (let level = min + 1; level <= Math.min(maxLevel, topLevel(c)); level++) {
      const candidate = best.get(`${c.id}:${CATEGORIES[level][0]}`), current = best.get(`${c.id}:${CATEGORIES[pick][0]}`);
      if (candidate && (!current || candidate.time < current.time)) pick = level;
    }
    return row(CATEGORIES[pick][0], CATEGORIES[pick][1], best.get(`${c.id}:${CATEGORIES[pick][0]}`));
  };
  const section = (label: string, list: Course[]) => list.length > 0 && <>
    <label className="sectionLabel">{label}</label>
    {list.map(c => <div className="courseGroup" key={c.id}>
      <strong className="courseName">{courseLabel(c, c.id)}</strong>
      {executable(c) ?? CATEGORIES.slice(c.minLevel ?? 0, topLevel(c) + 1).map(([cat, title]) => row(cat, title, best.get(`${c.id}:${cat}`)))}
    </div>)}
  </>;
  return <div className="courseList">
    {chains.length > 0 && <><label className="sectionLabel">CHAINS</label>
      {chains.map(chain => <div className="courseGroup" key={chain.name}>
        <div className="chainHeader"><strong className="courseName">{chain.name} · {chain.capabilities.toUpperCase()}{chain.total != null ? ` · ${chain.total.toFixed(2)}s` : ""}</strong>
          <button className="chainPlay" disabled={running || !chainPlayable(chain)} title={chainPlayable(chain) ? "Play the whole chain" : "Every segment needs a route first"} onClick={() => onCommand("/chains/run", { chain: chain.name })}>▶ Play</button></div>
        {chain.error ? <div className="categoryRow empty"><span>{chain.error}</span><b>!</b></div>
          : chain.segments.map(s => s.routeId
            ? <button key={s.index} className={`categoryRow ${s.routeId === activeId ? "active" : ""}`} onClick={() => onCommand("/routes/select", { routeId: s.routeId })}><span>{s.restart ? "↺ " : ""}{segmentLabel(s)}</span><b>{s.time?.toFixed(2)}s</b></button>
            : <div key={s.index} className="categoryRow empty"><span>{s.restart ? "↺ " : ""}{segmentLabel(s)}</span><b>—</b></div>)}
      </div>)}</>}
    {section("COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id))}
    {section("TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id))}
    {section("BONUSES", courses.filter(c => c.bonus).sort((a, b) => a.id - b.id))}
    {other.length > 0 && <details className="otherRoutes"><summary className="sectionLabel">OTHER ROUTES ({other.length})</summary>
      <div className="routeList">{other.map(item => <div className={`routeRow ${item.id === activeId ? "active" : ""}`} key={item.id} onContextMenu={(event) => { event.preventDefault(); if (!item.favorite && window.confirm(`Delete route “${item.name}”?`)) onCommand("/routes/delete", { routeId: item.id }); }}><button className="routeItem" onClick={() => onCommand("/routes/select", { routeId: item.id })}><div><strong>{item.favorite ? "★ " : ""}{item.name}</strong><small>{item.actions.length} actions{item.favorite ? " · read-only" : item.hotkey ? ` · ${item.hotkey}` : ""}</small></div></button><button className={`favoriteButton ${item.favorite?"active":""}`} title={item.favorite?"Unfavorite and unlock":"Favorite and lock"} onClick={()=>onCommand("/routes/favorite",{routeId:item.id,favorite:!item.favorite})}>{item.favorite?"★":"☆"}</button></div>)}</div>
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
    <div className="offlineSummary"><div><span>Best</span><strong>{solver.best!=null?`${solver.best.toFixed(2)}s`:"—"}</strong><small>{solver.chain?`Chain ${solver.chain} · segment ${(solver.segment??0)+1} · ${solver.capabilities}`:solver.course?`${courseLabel(courses.find(c=>c.id===solver.course),solver.course)} · ${solver.capabilities}`:"Not started"}</small></div><div><span>Runs</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.finishes.toLocaleString()} timed finishes</small></div><div><span>Turbo checks</span><strong>{solver.turboChecks.toLocaleString()}</strong><small>{solver.turboSaved.toLocaleString()} saved as routes</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.log.length>0&&<ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul>}
  </div>;
}

// Each chain in order: run it (or up to a segment), and solve a segment from where the routes before it end.
function Chains({chains,chainRun,solver,running,onCommand}:{chains:Chain[];chainRun?:ChainRun;solver:SolverState;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const live = chainRun?.running;
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE GAME IN ORDER</p><h2>Chains</h2></div><span className={`offlineStatus ${live?"running":""}`}>{live?"Playing":"Idle"}</span></div>
    <p className="lede">A chain plays its segments back to back: each starts exactly where the one before it ended (position, speed, held jump), so each segment is solved from there. ↺ is the game&apos;s quick restart before a segment. Solving a segment searches offline from the end of the chain&apos;s routes before it; a faster segment re-times the ones after it and marks those that no longer hold up as stale (they stay as seeds).</p>
    {live&&<p className="offlineConfirmation">Playing chain {live.chain}: segment {live.segment+1} of {live.of} ({live.token}){live.splits.length?` · ${live.splits.map(t=>t.toFixed(2)).join(" + ")}`:""}</p>}
    {chainRun?.last&&<p className="offlineConfirmation">{chainRun.last}</p>}
    {chains.map(chain=><div key={chain.name} className="chainBlock">
      <div className="panelTitle"><div><p className="eyebrow">{chain.capabilities.toUpperCase()}</p><h2>{chain.name}{chain.total!=null?` · ${chain.total.toFixed(2)}s`:""}</h2></div>
        <button className="primary" disabled={running||!chainPlayable(chain)} onClick={()=>onCommand("/chains/run",{chain:chain.name})}>▶ Run chain</button></div>
      {chain.error?<p className="errorBanner">{chain.error}</p>:<div className="resultTable">
        <div className="tableHead"><span>Segment</span><span>Time</span><span>Route</span><span></span></div>
        {chain.segments.map((s,i)=>{ const ready=chain.segments.slice(0,i).every(p=>!!p.routeId); return <div className="tableRow" key={s.index}>
          <span>{i+1}. {s.restart?"↺ ":""}{segmentLabel(s)}</span>
          <span>{s.time!=null?`${s.time.toFixed(2)}s`:"—"}</span>
          <span>{s.routeId?<button className="secondary" onClick={()=>onCommand("/routes/select",{routeId:s.routeId})}>Select</button>:"none"}{s.staleRoutes>0?` · ${s.staleRoutes} stale`:""}</span>
          <span className="buttonRow"><button className="secondary" disabled={running||!s.routeId||!ready} onClick={()=>onCommand("/chains/run",{chain:chain.name,through:s.index})}>▶ To here</button><button className="primary" disabled={solver.running||running||!ready} title={ready?"Solve offline from where the routes before it end":"Solve the segments before it first"} onClick={()=>onCommand("/solver/start",{chain:chain.name,segment:s.index})}>Solve</button></span>
        </div>; })}
      </div>}
    </div>)}
  </div>;
}
