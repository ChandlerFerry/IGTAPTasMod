"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import "./action-editor.css";
import "./frame-timeline.css";
import "./favorite-ui.css";

const API = "http://127.0.0.1:17426/api";

type Action = { id: string; label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number; enabled: boolean; rawMoveX?: number; rawMoveY?: number; rawJumpPressed?: boolean; rawJumpHeld?: boolean; rawJumpReleased?: boolean; rawDashPressed?: boolean; rawDashHeld?: boolean };
type Route = { id: string; name: string; favorite: boolean; hotkey: string; checkpointId: string; actions: Action[] };
type RecordedAction = { label: string; type: string; direction: number; wallJumpHoldDirection: number; durationTicks: number };
type Recording = { id: string; name: string; favorite: boolean; createdUtc: string; sampleCount: number; actionCount: number; courseTimeSeconds: number; actions: RecordedAction[] };
type Checkpoint = { id: string; name: string; createdUtc: string; assigned: boolean };
type Result = { id: string; success: boolean; completionTime: number; completionTicks: number; timingSource?: string; deterministicPhysics?: boolean; failureReason?: string; timestampUtc: string };
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; evaluations: number; finishes: number; cells: number; best: number | null; log: string[]; courses: number[] };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean;
  selectedRouteId: string; routes: Route[]; recordings: Recording[]; checkpoints: Checkpoint[]; results: Result[]; solver: SolverState;
};

const emptyState: StudioState = { connected: false, version: "", engineStatus: "Connecting…", running: false, recording: false, paused: false, capabilitiesLocked: false, selectedRouteId: "", routes: [], recordings: [], checkpoints: [], results: [], solver: { running: false, status: "Idle", course: 0, capabilities: "", evaluations: 0, finishes: 0, cells: 0, best: null, log: [], courses: [1, 2, 3] } };

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
      <label className="sectionLabel">ROUTES</label>
      <div className="routeList">{state.routes.map((item, index) => <div className={`routeRow ${item.id === route?.id ? "active" : ""}`} key={item.id} onContextMenu={(event) => { event.preventDefault(); if (!item.favorite && window.confirm(`Delete route “${item.name}”?`)) command("/routes/delete", { routeId: item.id }); }}><button className="routeItem" onClick={() => command("/routes/select", { routeId: item.id })}><span>{index + 1}</span><div><strong>{item.favorite ? "★ " : ""}{item.name}</strong><small>{item.actions.length} actions{item.favorite ? " · read-only" : item.hotkey ? ` · ${item.hotkey}` : ""}</small></div></button><button className={`favoriteButton ${item.favorite?"active":""}`} title={item.favorite?"Unfavorite and unlock":"Favorite and lock"} onClick={()=>command("/routes/favorite",{routeId:item.id,favorite:!item.favorite})}>{item.favorite?"★":"☆"}</button></div>)}</div>
      <div className="sidebarFooter"><p>The game keeps simulating while this window is focused.</p><kbd>F8</kbd><span>Emergency stop</span></div>
    </aside>

    <section className="workspace">
      <label className={`capabilityLock ${state.capabilitiesLocked ? "on" : ""}`}>
        <input type="checkbox" checked={state.capabilitiesLocked} disabled={!state.connected} onChange={e => command("/settings/lock-capabilities", { locked: e.target.checked })} />
        <strong>DO NOT MODIFY CAPABILITIES</strong>
        <span>{state.capabilitiesLocked ? "On: the plugin never changes your unlocks. The solver and turbo tools are disabled." : "Off: category routes and the solver set wall jump / dash for their category."}</span>
      </label>
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

      <nav className="tabs">{[["overview","Overview"],["timeline","Timeline"],["recordings","Recordings"],["optimize","Optimize"],["checkpoints","Checkpoints"],["results","Results"]].map(([id,label]) => <button key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)}>{label}</button>)}</nav>
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

        {tab === "recordings" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">RAW FIXED-FRAME CAPTURES</p><h2>Recordings</h2></div><span className="hint">{state.recording ? "Recording now…" : `${state.recordings.length} saved`}</span></div>{state.recording&&<p className="recordingActive">● Recording is active. Finishing the course saves it automatically.</p>}<div className="recordingList">{state.recordings.map(recording=><div className={`recordingCard ${recording.favorite?"locked":""}`} key={recording.id}><div className="recordingTitle"><div><strong>{recording.favorite?"★ ":""}{recording.name}</strong><small>{new Date(recording.createdUtc).toLocaleString()} · {recording.sampleCount} physics frames · {recording.actionCount} actions{recording.favorite?" · read-only":""}</small></div>{recording.courseTimeSeconds>0&&<b>{recording.courseTimeSeconds.toFixed(3)}s</b>}</div><p>{recording.actions.map(action=>`${action.type.toLowerCase()} ${DirectionText(action.direction)} ${action.durationTicks}f${action.type==="WALLJUMP"&&action.wallJumpHoldDirection>=-1&&action.wallJumpHoldDirection<=1?` (hold ${DirectionText(action.wallJumpHoldDirection)})`:""}`).join(" → ")}</p><div className="buttonRow"><button className="primary" onClick={()=>command("/recordings/create-route",{recordingId:recording.id})}>Create route</button><button disabled={recording.favorite} onClick={()=>{const name=window.prompt("Rename recording",recording.name);if(name?.trim())command("/recordings/rename",{recordingId:recording.id,name:name.trim()});}}>Rename</button><button className={`favoriteButton ${recording.favorite?"active":""}`} onClick={()=>command("/recordings/favorite",{recordingId:recording.id,favorite:!recording.favorite})}>{recording.favorite?"★ Locked":"☆ Favorite"}</button><button className="danger" disabled={recording.favorite} onClick={()=>{if(window.confirm(`Delete recording “${recording.name}”?`)) command("/recordings/delete",{recordingId:recording.id});}}>Delete</button></div></div>)}</div>{!state.recordings.length&&!state.recording&&<div className="emptyState"><span className="orbit">●</span><h2>No recordings yet</h2><p>Press Record, play the route, and finish the course.</p></div>}</div>}

        {tab === "optimize" && <Solver solver={state.solver ?? emptyState.solver} onCommand={command}/>} 

        {tab === "checkpoints" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">START STATES</p><h2>Section checkpoints</h2></div><button className="primary" onClick={() => command("/checkpoints/capture")}>Capture current state</button></div><p className="lede">Assign a checkpoint to optimize an isolated section. Compatibility fingerprints prevent invalid restores.</p><div className="checkpointList">{state.checkpoints.map(cp => <div className={`checkpoint ${cp.assigned ? "assigned" : ""}`} key={cp.id}><div><strong>{cp.name}</strong><small>{new Date(cp.createdUtc).toLocaleString()}</small></div>{cp.assigned && <span className="badge">Route start</span>}<button onClick={() => command("/checkpoints/restore", { checkpointId: cp.id })}>Restore</button><button className="secondary" onClick={() => command("/checkpoints/assign", { checkpointId: cp.assigned ? "" : cp.id })}>{cp.assigned ? "Unassign" : "Use as start"}</button></div>)}</div></div>}

        {tab === "results" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">ATTEMPT HISTORY</p><h2>Results</h2></div><span className="hint">{state.results.length} attempts</span></div><div className="resultTable"><div className="tableHead"><span>Outcome</span><span>Time</span><span>Frames</span><span>When</span></div>{state.results.map(r => <div className="tableRow" key={r.id}><span className={r.success ? "success" : "failure"}>{r.success ? "✓ Success" : "× Failed"}{r.deterministicPhysics ? " · canonical" : ""}</span><span title={r.timingSource}>{r.success ? `${r.completionTime.toFixed(3)}s` : r.failureReason}</span><span>{r.completionTicks}</span><span>{new Date(r.timestampUtc).toLocaleString()}</span></div>)}</div></div>}
      </div>
    </section>
  </main>;
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
function Solver({solver,onCommand}:{solver:SolverState;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const [course,setCourse]=useState<number|"">("");
  const [caps,setCaps]=useState("wj");
  const chosen=course===""?(solver.courses[0]??1):course;
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">REAL-GAME SOLVER</p><h2>Solve a course</h2></div><span className={`offlineStatus ${solver.running?"running":""}`}>{solver.running?"Solving":"Idle"}</span></div>
    <p className="lede">Runs the real game fast-forwarded. It explores new paths from the start and sharpens the best finish; only the start and stop timer gates matter. Every new best is saved as a route and replaces slower ones.</p>
    <div className="offlineControls">
      <Field label="1. Course"><select value={chosen} disabled={solver.running} onChange={e=>{ if(e.target.value==="new") onCommand("/courses/add-from-recording").then(()=>setCourse(Math.max(0,...solver.courses)+1)); else setCourse(Number(e.target.value)); }}>{solver.courses.map(id=><option key={id} value={id}>Course {id}</option>)}<option value="new">+ New course from last recording</option></select></Field>
      <Field label="2. Capabilities"><select value={caps} disabled={solver.running} onChange={e=>setCaps(e.target.value)}><option value="none">Nothing</option><option value="wj">Wall jump</option><option value="wjdash">Wall jump + dash</option></select></Field>
    </div>
    <div className="buttonRow">{solver.running?<button className="danger" onClick={()=>onCommand("/solver/stop")}>Stop</button>:<button className="primary" onClick={()=>onCommand("/solver/start",{course:chosen,capabilities:caps})}>3. Solve</button>}</div>
    <div className="offlineSummary"><div><span>Best</span><strong>{solver.best!=null?`${solver.best.toFixed(2)}s`:"—"}</strong><small>{solver.course?`Course ${solver.course} · ${solver.capabilities}`:"Not started"}</small></div><div><span>Runs</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.finishes.toLocaleString()} timed finishes</small></div><div><span>Explored</span><strong>{solver.cells.toLocaleString()}</strong><small>map cells reached</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.log.length>0&&<ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul>}
  </div>;
}
