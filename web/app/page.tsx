"use client";

import { Fragment, useCallback, useEffect, useMemo, useState } from "react";
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
// `queue`: the run's targets in order (one for a single Solve), each with how it ended once it has. `repeat`: the queue
// starts over when it ends (pass 2, 3, ...), `minutes` doubling each pass, until stopped.
type SolverState = { running: boolean; status: string; speed?: string; course: number; capabilities: string; chain?: string | null; segment?: number; evaluations: number; finishes: number; turboChecks: number; turboSaved: number; toValidate?: number; best: number | null; log: string[]; courses: number[]; minutes?: number; repeat?: boolean; pass?: number; queue?: { label: string; result?: string | null; active: boolean }[] };
// A chain: the game in order, each segment starting where the one before ended ("restart" = the game's quick restart first).
// `fromNewGame`: it starts from a fresh new game. A segment's `key` names its routes: its token, "<token>#<k>" for a repeat;
// `category` is its own (the chain's, WJ after a buywj); `module`: the included chain ("chain:wj") it comes from.
type ChainSegment = { index: number; token: string; key: string; category: string; restart: boolean; module?: string | null; bonus: boolean; purchase: boolean; from?: string; to?: string; routeId?: string | null; time?: number | null; staleRoutes: number };
type Chain = { name: string; capabilities: string; fromNewGame?: boolean; error?: string | null; segments: ChainSegment[]; total?: number | null };
type ChainRun = { running: { chain: string; segment: number; of: number; token: string; splits: number[] } | null; last?: string | null };
type StudioState = {
  connected: boolean; version: string; engineStatus: string; running: boolean; recording: boolean; paused: boolean; capabilitiesLocked: boolean; overlayShown: boolean;
  // The widest category the save's own unlocks can run (0 NO_WJ … 3 WJ_DASH_AJ); null when the player isn't loaded.
  unlockLevel?: number | null;
  // A fresh new game is loaded (/api/session/newgame): chains from "newgame" can be solved.
  freshGame?: boolean; newGameLoading?: boolean;
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
  const routeChain = route ? chainRoutes(state.chains ?? []).get(route.id)?.chain : undefined;
  const successes = state.results.filter(r => r.success);
  const successRate = state.results.length ? successes.length / state.results.length * 100 : 0;
  const bestResult = successes.length ? successes.reduce((best,current)=>current.completionTime<best.completionTime?current:best) : null;

  return <main className="shell">
    <aside className="sidebar">
      <div className="brand"><span className="brandMark">T</span><div><strong>IGTAP</strong><small>TAS STUDIO</small></div></div>
      <div className={`connection ${state.connected ? "online" : "offline"}`}><i />{state.connected ? `Connected · v${state.version}` : "Waiting for game"}</div>
      <RouteSidebar routes={state.routes} courses={state.courses ?? []} chains={state.chains ?? []} activeId={route?.id} unlockLevel={unlockLevel} running={state.running} onCommand={command} />
      <div className="sidebarFooter"><p>The game keeps simulating while this window is focused.</p><kbd>F8</kbd><span>Emergency stop</span></div>
    </aside>

    <section className="workspace">
      <div className="toggleBar">
        <label className={`capabilityLock ${state.capabilitiesLocked ? "on" : ""}`} title={state.capabilitiesLocked ? "On: wall jump / dash on your player are never changed, so your save stays as it is. Solving and turbo tools only work for the new-game run (it gets its unlocks from the game's own purchases)." : "Off: routes and the solver switch wall jump / dash on your player to match their category, and the game autosaves that."}>
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
          {routeChain && <button className="primary" disabled={state.running || !chainPlayable(routeChain)} title={chainPlayable(routeChain) ? `Play all ${routeChain.segments.length} segments of ${routeChain.name}` : "Every segment needs a route first"} onClick={() => command("/chains/run", { chain: routeChain.name })}>▶ Run whole chain</button>}
          <button className="secondary" disabled={!route || state.running} onClick={() => command("/routes/duplicate")}>Duplicate</button>
          <button className="secondary" disabled={!route || route.favorite} onClick={() => { const name=window.prompt("Rename route",route?.name); if(name?.trim()) command("/routes/rename",{routeId:route?.id,name:name.trim()}); }}>Rename</button>
          <button className={`favoriteButton topFavorite ${route?.favorite?"active":""}`} disabled={!route} onClick={()=>command("/routes/favorite",{routeId:route?.id,favorite:!route?.favorite})}>{route?.favorite?"★ Locked":"☆ Favorite"}</button>
          <button className="record" disabled={!route || state.running} onClick={() => command(state.recording ? "/record/stop" : "/record/start")}>{state.recording ? "■ Stop recording" : "● Record"}</button>
          <button className="danger" disabled={!state.running && !state.recording} onClick={() => command("/routes/stop")}>Stop</button>
        </div>
      </header>

      <nav className="tabs">{[["overview","Overview"],["timeline","Timeline"],["recordings","Recordings"],["chains","Runs"],["optimize","Optimize"],["checkpoints","Checkpoints"],["results","Results"]].map(([id,label]) => <button key={id} className={tab === id ? "active" : ""} onClick={() => setTab(id)}>{label}</button>)}</nav>
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

        {tab === "chains" && <Chains chains={state.chains ?? []} chainRun={state.chainRun} solver={state.solver ?? emptyState.solver} running={state.running} freshGame={!!state.freshGame} newGameLoading={!!state.newGameLoading} onCommand={command}/>}

        {tab === "optimize" && <div className="optimizeGrid"><Solver solver={state.solver ?? emptyState.solver} courses={state.courses ?? []} onCommand={command}/><SolveMatrix routes={state.routes} courses={state.courses ?? []} chains={state.chains ?? []} solver={state.solver ?? emptyState.solver} onCommand={command}/></div>}

        {tab === "checkpoints" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">START STATES</p><h2>Section checkpoints</h2></div><button className="primary" onClick={() => command("/checkpoints/capture")}>Capture current state</button></div><p className="lede">Assign a checkpoint to optimize an isolated section. Compatibility fingerprints prevent invalid restores.</p><div className="checkpointList">{state.checkpoints.map(cp => <div className={`checkpoint ${cp.assigned ? "assigned" : ""}`} key={cp.id}><div><strong>{cp.name}</strong><small>{new Date(cp.createdUtc).toLocaleString()}</small></div>{cp.assigned && <span className="badge">Route start</span>}<button onClick={() => command("/checkpoints/restore", { checkpointId: cp.id })}>Restore</button><button className="secondary" onClick={() => command("/checkpoints/assign", { checkpointId: cp.assigned ? "" : cp.id })}>{cp.assigned ? "Unassign" : "Use as start"}</button></div>)}</div></div>}

        {tab === "results" && <div className="panel"><div className="panelTitle"><div><p className="eyebrow">ATTEMPT HISTORY</p><h2>Results</h2></div><span className="hint">{state.results.length} attempts</span></div><div className="resultTable"><div className="tableHead"><span>Outcome</span><span>Time</span><span>Frames</span><span>When</span></div>{state.results.map(r => <div className="tableRow" key={r.id}><span className={r.success ? "success" : "failure"}>{r.success ? "✓ Success" : "× Failed"}{r.deterministicPhysics ? " · canonical" : ""}</span><span title={r.timingSource}>{r.success ? `${r.completionTime.toFixed(3)}s` : r.failureReason}</span><span>{r.completionTicks}</span><span>{new Date(r.timestampUtc).toLocaleString()}</span></div>)}</div></div>}
      </div>
    </section>
  </main>;
}

const CATEGORIES: [string, string][] = [["NO_WJ", "No wall jump"], ["WJ", "Wall jump"], ["WJDASH", "Wall jump + dash"], ["WJ_DASH_AJ", "Wall jump + dash + air jump"]];
const CATEGORY_ROUTE = /^([^_\s]+)_(NO_WJ|WJDASH|WJ_DASH_AJ|WJ) ([\d.]+)$/i;

const place = (p: string) => /^\d+$/.test(p) ? `Course ${p}` : p === "newgame" ? "New game" : p.replace(/^bonus/i, "Bonus ");

function courseLabel(c: Course | undefined, id: number) {
  if (!c) return `Course ${id}`;
  return c.to ? `${place(c.from ?? "")} → ${place(c.to)}` : place(c.token);
}

const PURCHASES: Record<string, string> = { buyreward: "Buy reward", buyclone: "Buy clone", buywj: "Buy wall jump", buydash: "Buy dash" };

// "wj › Course 1 #12 · WJ": a repeat shows its occurrence, a part of an included chain that chain, every segment its category.
function segmentLabel(s: ChainSegment) {
  const what = s.to ? `${place(s.from ?? "")} → ${place(s.to)}` : s.purchase ? s.token.split("+").map(t => PURCHASES[t] ?? t).join(" + ") : place(s.token);
  const occurrence = s.key !== s.token ? ` #${s.key.slice(s.token.length + 1)}` : "";
  return `${s.module ? `${s.module} › ` : ""}${what}${occurrence} · ${s.category}`;
}

// A chain's segments in runs: each part ("chain:wj") on its own, the chain's own segments between them.
type SegmentGroup = { module: string | null; segments: ChainSegment[] };
function segmentGroups(chain: Chain): SegmentGroup[] {
  const groups: SegmentGroup[] = [];
  for (const s of chain.segments) {
    const module = s.module ?? null, last = groups[groups.length - 1];
    if (last && last.module === module) last.segments.push(s); else groups.push({ module, segments: [s] });
  }
  return groups;
}

// What a run holds: "wj part"; the chain's own segments by what they end with ("Economy → Buy wall jump" when they buy
// something), else their first and last.
function groupTitle(g: SegmentGroup) {
  if (g.module) return `${g.module} part`;
  const what = (s: ChainSegment) => segmentLabel({ ...s, key: s.token, module: null }).replace(/ · [A-Z_]+$/, "");
  const bought = g.segments.filter(s => s.purchase);
  if (g.segments.length === 1) return what(g.segments[0]);
  return bought.length > 0 ? `Economy → ${what(bought[bought.length - 1])}` : `${what(g.segments[0])} … ${what(g.segments[g.segments.length - 1])}`;
}

// "1–55 · 2/55": the run's segment numbers and how many have a route; its time once every one has.
function groupStats(g: SegmentGroup) {
  const solved = g.segments.filter(s => s.routeId).length, first = g.segments[0].index + 1, last = g.segments[g.segments.length - 1].index + 1;
  return { range: first === last ? `${first}` : `${first}–${last}`, solved, total: g.segments.length,
    time: solved === g.segments.length ? g.segments.reduce((sum, s) => sum + (s.time ?? 0), 0) : null };
}

function chainTitle(c: Chain) { return c.fromNewGame ? (c.name === "newgame" ? "New game" : `New game (${c.name})`) : `Chain ${c.name}`; }

// Every chain segment's route, by route id (a token may repeat, so each route maps to exactly one segment).
function chainRoutes(chains: Chain[]) {
  const byRoute = new Map<string, {chain: Chain; segment: ChainSegment}>();
  for (const chain of chains) for (const segment of chain.segments) if (segment.routeId) byRoute.set(segment.routeId, {chain, segment});
  return byRoute;
}

// A chain plays end to end only when every segment has a route.
function chainPlayable(c: Chain) { return !c.error && c.segments.every(s => !!s.routeId); }

// The widest category a course has: a bonus only has its first one (the unlocks it needs), everything else runs in every wider one too.
function topLevel(c: Course) { return c.bonus ? c.minLevel ?? 0 : CATEGORIES.length - 1; }

// The fastest category route per "<course id>:<CAPS>"; slower ones and routes that aren't a known course's category
// routes go to `other`. A chain's routes (listed under their chain) are in neither.
function categoryBests(routes: Route[], courses: Course[], chains: Chain[]) {
  const best = new Map<string, {route: Route; time: number}>();
  const other: Route[] = [];
  const listed = chainRoutes(chains);
  for (const r of routes) {
    if (listed.has(r.id)) continue;
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

// Two things: the full run from a new game, and every place it goes through (course, transition, bonus) by category,
// its fastest solo route (from its own start) beside its time inside the run. A solo route runs in every wider
// category, so a category's solo time is the fastest over it and the narrower ones; Δ is run minus solo (a place
// repeated in the run shows its slowest solved pass). A category wider than `unlockLevel` (the save's unlocks) is dimmed:
// your save can't run it by hand.
function RouteSidebar({routes,courses,chains,activeId,unlockLevel,running,onCommand}:{routes:Route[];courses:Course[];chains:Chain[];activeId?:string;unlockLevel:number|null;running:boolean;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const run = chains.find(c => c.fromNewGame);
  const segments = run && !run.error ? run.segments : [];
  // Places the run goes through that aren't known courses ("newgameto1") are listed too, in run order; purchases aren't places.
  const places = [...courses];
  for (const s of segments) if (!s.purchase && !places.some(c => c.token === s.token))
    places.push({ id: s.index - segments.length, token: s.token, bonus: s.bonus, minLevel: 0, from: s.from, to: s.to });
  const {best, other} = categoryBests(routes, places, run ? [run] : []);
  const cell = (routeId: string | undefined, text: string, title: string) => routeId
    ? <button className={`cmpCell ${routeId === activeId ? "active" : ""}`} title={title} onClick={() => onCommand("/routes/select", { routeId })}>{text}</button>
    : <span className="cmpCell empty" title={title}>{text}</span>;
  const rows = (c: Course) => CATEGORIES.slice(c.minLevel ?? 0, topLevel(c) + 1).flatMap(([cat], i) => {
    let solo: {route: Route; time: number} | undefined;
    for (const [narrower] of CATEGORIES.slice(c.minLevel ?? 0, (c.minLevel ?? 0) + i + 1)) {
      const b = best.get(`${c.id}:${narrower}`);
      if (b && (!solo || b.time < solo.time)) solo = b;
    }
    const passes = segments.filter(s => s.token === c.token && s.category === cat);
    // A category with no route of its own and no pass in the run would only repeat a narrower one's row.
    if (!best.has(`${c.id}:${cat}`) && passes.length === 0) return [];
    const solved = passes.filter(s => s.routeId && s.time != null);
    const slowest = solved.reduce<ChainSegment | undefined>((w, s) => !w || s.time! > w.time! ? s : w, undefined);
    const delta = solo && slowest ? slowest.time! - solo.time : null;
    const runTitle = passes.length === 0 ? "Not in the new game run" : `${solved.length}/${passes.length} passes solved: ${passes.map(s => `#${s.index + 1} ${s.time != null ? s.time.toFixed(2) : "—"}`).join(", ")}`;
    const beyond = unlockLevel != null && (c.minLevel ?? 0) + i > unlockLevel;
    return [<div className={`cmpRow ${beyond ? "beyondUnlocks" : ""}`} key={cat} title={beyond ? `Your save's unlocks only reach ${CATEGORIES[unlockLevel][1]}` : undefined}>
      <span>{SHORT_CATEGORY[cat]}{passes.length > 1 && <small> ×{passes.length}</small>}</span>
      {cell(solo?.route.id, solo ? solo.time.toFixed(2) : "—", solo?.route.name ?? "No solo route")}
      {cell(slowest?.routeId ?? undefined, slowest ? slowest.time!.toFixed(2) : passes.length ? "—" : "", runTitle)}
      <span className={`cmpDelta ${delta == null ? "" : delta > 0.005 ? "behind" : "ahead"}`}>{delta == null ? "" : Math.abs(delta) <= 0.005 ? "=" : `${delta > 0 ? "+" : "−"}${Math.abs(delta).toFixed(2)}`}</span>
    </div>];
  });
  const section = (label: string, list: Course[]) => {
    const groups = list.map(c => ({c, r: rows(c)})).filter(g => g.r.length > 0);
    return groups.length > 0 && <>
      <div className="cmpHead"><label className="sectionLabel">{label}</label><span>Solo</span><span>Run</span><span>Δ</span></div>
      {groups.map(({c, r}) => <div className="courseGroup" key={c.token}><strong className="courseName">{courseLabel(c, c.id)}</strong>{r}</div>)}
    </>;
  };
  const runCard = run && (() => {
    const solved = run.segments.filter(s => s.routeId), next = run.segments.find(s => !s.routeId);
    const sum = solved.reduce((t, s) => t + (s.time ?? 0), 0);
    return <div className="runCard">
      <div className="runHead"><strong>New game</strong><b className={run.total == null ? "partial" : ""} title={run.total == null ? "Solved segments so far" : "Whole run"}>{(run.total ?? sum).toFixed(2)}s</b>
        <button className="chainPlay" disabled={running || !chainPlayable(run)} title={chainPlayable(run) ? "Play the whole run" : "Every segment needs a route first"} onClick={() => onCommand("/chains/run", { chain: run.name })}>▶</button></div>
      <div className="runBar"><i style={{ width: `${solved.length / Math.max(1, run.segments.length) * 100}%` }} /></div>
      {run.error ? <small className="runError">{run.error}</small>
        : <small>{solved.length}/{run.segments.length} segments{next && <> · next <b>{next.index + 1}. {next.restart ? "↺ " : ""}{segmentLabel({ ...next, module: null })}</b></>}</small>}
    </div>;
  })();
  return <div className="courseList">
    {runCard}
    {section("COURSES", places.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id))}
    {section("TRANSITIONS", places.filter(c => c.to).sort((a, b) => a.id - b.id))}
    {section("BONUSES", places.filter(c => c.bonus && !c.to).sort((a, b) => a.id - b.id))}
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
    <div className="offlineSummary"><div><span>Best</span><strong>{solver.best!=null?`${solver.best.toFixed(2)}s`:"—"}</strong><small>{solver.chain?`Chain ${solver.chain} · segment ${(solver.segment??0)+1} · ${solver.capabilities}`:solver.course?`${courseLabel(courses.find(c=>c.id===solver.course),solver.course)} · ${solver.capabilities}`:"Not started"}</small></div><div><span>Runs</span><strong>{solver.evaluations.toLocaleString()}</strong><small>{solver.finishes.toLocaleString()} timed finishes</small></div><div><span>Turbo checks</span><strong>{solver.turboChecks.toLocaleString()}</strong><small>{solver.turboSaved.toLocaleString()} saved as routes</small></div><div><span>To validate</span><strong>{(solver.toValidate??0).toLocaleString()}</strong><small>offline bests waiting for a turbo check</small></div></div>
    <p className="offlineConfirmation">{solver.status}</p>
    {solver.running&&solver.speed&&<p className="offlineConfirmation">{solver.speed}</p>}
    {solver.log.length>0&&<ul className="solverLog">{solver.log.map((line,i)=><li key={i}>{line}</li>)}</ul>}
  </div>;
}

// The solver's capability values, indexed like CATEGORIES.
const CAPS = ["none", "wj", "wjdash", "wjdashaj"];
type SolveTarget = { key: string; body: { course: number; capabilities: string } | { chain: string; segment: number }; time?: number };

// How an optimize run spends its time (kept in the browser, shared by Runs and Optimize): `minutes` per target, and with
// `repeat` the queue starts over when it ends, each pass with twice the minutes up to `maxMinutes`, until stopped. Short
// first passes get every segment a route fast; later ones dig deeper (leave it overnight).
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

// Batch optimize: every course's categories and every chain segment as a checkbox, solved one after another in the
// matrix's order (top to bottom, left to right: a chain's earlier segments before its later ones). The selection and
// passes are kept in the browser.
function SolveMatrix({routes,courses,chains,solver,onCommand}:{routes:Route[];courses:Course[];chains:Chain[];solver:SolverState;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [passes, updatePasses] = usePasses();
  // Runs of a chain's segments opened by hand ("<chain>:<first index>"); closed until then.
  const [opened, setOpened] = useState<Set<string>>(new Set());
  useEffect(() => setSelected(new Set(JSON.parse(window.localStorage.getItem("optimizeSelection") ?? "[]"))), []);
  const select = (next: Set<string>) => { setSelected(next); window.localStorage.setItem("optimizeSelection", JSON.stringify([...next])); };
  // Ticks every key, unless all of them already are: then clears them.
  const toggle = (keys: string[]) => {
    const next = new Set(selected), on = keys.some(k => !next.has(k));
    for (const k of keys) { if (on) next.add(k); else next.delete(k); }
    select(next);
  };
  const {best} = categoryBests(routes, courses, chains);
  const groups: [string, Course[]][] = [
    ["COURSES", courses.filter(c => !c.bonus && !c.to).sort((a, b) => a.id - b.id)],
    ["TRANSITIONS", courses.filter(c => c.to).sort((a, b) => a.id - b.id)],
    ["BONUSES", courses.filter(c => c.bonus).sort((a, b) => a.id - b.id)]];
  const cells = (c: Course): (SolveTarget | null)[] => CATEGORIES.map(([cat], level) => level < (c.minLevel ?? 0) || level > topLevel(c) ? null
    : { key: `c:${c.id}:${CAPS[level]}`, body: { course: c.id, capabilities: CAPS[level] }, time: best.get(`${c.id}:${cat}`)?.time });
  const segments = (chain: Chain): SolveTarget[] => chain.segments.map(s => ({ key: `s:${chain.name}:${s.index}`, body: { chain: chain.name, segment: s.index }, time: s.time ?? undefined }));
  const playable = chains.filter(c => c.fromNewGame && !c.error);
  const all = [...playable.flatMap(segments), ...groups.flatMap(([, list]) => list.flatMap(c => cells(c).filter((t): t is SolveTarget => !!t)))];
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
        {playable.map(chain => <Fragment key={chain.name}>
          <tr className="matrixSection"><th colSpan={5}><button onClick={() => toggle(segments(chain).map(t => t.key))}>{chainTitle(chain).toUpperCase()} · {chain.capabilities.toUpperCase()}</button></th></tr>
          {segmentGroups(chain).map(g => { const key = `${chain.name}:${g.segments[0].index}`, open = opened.has(key), st = groupStats(g);
            const chainTargets = segments(chain), targets = g.segments.map(s => chainTargets[s.index]);
            return <Fragment key={key}>
              <tr className="matrixPart"><th><button onClick={() => { const next = new Set(opened); if (open) next.delete(key); else next.add(key); setOpened(next); }}>{open ? "▾" : "▸"} {groupTitle(g)} <small>{st.range} · {st.solved}/{st.total}</small></button></th>
                <td colSpan={4}><button title="Tick every segment of this run" onClick={() => toggle(targets.map(t => t.key))}>{targets.filter(t => selected.has(t.key)).length}/{targets.length} ticked</button></td></tr>
              {open && g.segments.map((s, i) => <tr key={targets[i].key}>
                <th>{s.index + 1}. {s.restart ? "↺ " : ""}{segmentLabel({ ...s, module: null })}</th>
                <td colSpan={4}>{checkbox(targets[i])}</td>
              </tr>)}
            </Fragment>; })}
        </Fragment>)}
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

// The full runs (from a new game), in collapsible runs of segments: its own and each part's ("chain:wj"). A part's own
// chain shows only inside that part. Run it (or up to a segment), solve a segment from where the routes
// before it end, or optimize the whole run: every segment in order, pass after pass.
function Chains({chains,chainRun,solver,running,freshGame,newGameLoading,onCommand}:{chains:Chain[];chainRun?:ChainRun;solver:SolverState;running:boolean;freshGame:boolean;newGameLoading:boolean;onCommand:(path:string,body?:unknown)=>Promise<void>}) {
  const live = chainRun?.running;
  const listed = chains.filter(c => c.fromNewGame);
  const [passes, updatePasses] = usePasses();
  return <div className="panel offlinePanel"><div className="panelTitle"><div><p className="eyebrow">THE GAME IN ORDER</p><h2>Runs</h2></div><span className={`offlineStatus ${live||solver.running?"running":""}`}>{live?"Playing":solver.running?"Solving":"Idle"}</span></div>
    <p className="lede">A run plays its segments back to back: each starts exactly where the one before it ended (position, speed, held jump, Cash), so each segment is solved from there. ↺ is the game&apos;s quick restart before a segment. A part (wj, dash) is another chain&apos;s segments in the run; its own routes seed the run&apos;s. Solving a segment searches offline from the end of the routes before it; a faster segment re-times the ones after it and marks those that no longer hold up as stale (they stay as seeds). <b>Optimize whole run</b> solves every segment in order, each for its minutes; looping, it starts over with twice the minutes each pass, so the next segments get routes quickly and the whole run keeps improving until Stop.</p>
    <PassFields passes={passes} update={updatePasses} disabled={solver.running} />
    {solver.running&&<div className="buttonRow runSolving"><span>{solver.chain?`Optimizing ${solver.chain}: segment ${(solver.segment??0)+1}`:"Optimizing"}{progressText(solver)?` · ${progressText(solver)}`:""}{solver.best!=null?` · best ${solver.best.toFixed(2)}s`:""}</span>
      <button className="secondary" onClick={()=>onCommand("/solver/skip")}>Skip segment</button><button className="danger" onClick={()=>onCommand("/solver/stop")}>Stop</button></div>}
    {solver.running&&<p className="offlineConfirmation">{solver.status}</p>}
    {live&&<p className="offlineConfirmation">Playing {live.chain}: segment {live.segment+1} of {live.of} ({live.token}){live.splits.length?` · ${live.splits.map(t=>t.toFixed(2)).join(" + ")}`:""}</p>}
    {chainRun?.last&&<p className="offlineConfirmation">{chainRun.last}</p>}
    {listed.map(chain=><div key={chain.name} className="chainBlock">
      <div className="panelTitle"><div><p className="eyebrow">{chain.fromNewGame?"FROM A NEW GAME · ":"STARTS AT ITS COURSE · "}{chain.capabilities.toUpperCase()} · {chain.segments.filter(s=>s.routeId).length}/{chain.segments.length} SEGMENTS</p><h2>{chainTitle(chain)}{chain.total!=null?` · ${chain.total.toFixed(2)}s`:""}</h2></div>
        <span className="buttonRow">{chain.fromNewGame&&<button className="secondary" disabled={running||newGameLoading} title="Solving a segment of a run from a new game needs a fresh game in memory (nothing is loaded or saved)" onClick={()=>onCommand("/session/newgame")}>{newGameLoading?"Loading new game…":freshGame?"↻ New game (fresh)":"Load new game"}</button>}
        <button className="primary" disabled={solver.running||running||newGameLoading||!!chain.error} title={passes.repeat?`Every segment in order, ${minutesText(passes.minutes)} each, then again with twice that (up to ${minutesText(passes.maxMinutes)}) until Stop`:`Every segment in order, up to ${minutesText(passes.minutes)} each`} onClick={()=>onCommand("/solver/start",{queue:chain.segments.map(s=>({chain:chain.name,segment:s.index})),...passes})}>⟳ Optimize whole run</button>
        <button className="secondary" disabled={solver.running||running||newGameLoading||!!chain.error||chain.segments.every(s=>s.routeId)} title={`Only the segments without a route, in order, ${minutesText(passes.minutes)} each; one that gets none is retried with twice that (up to ${minutesText(passes.maxMinutes)}) until every segment has a route`} onClick={()=>onCommand("/solver/start",{queue:chain.segments.filter(s=>!s.routeId).map(s=>({chain:chain.name,segment:s.index})),...passes,unsolved:true})}>⟳ Solve new segments</button>
        <button className="secondary" disabled={running||newGameLoading||!chainPlayable(chain)} title={chain.fromNewGame?"Loads a fresh new game, then plays the run from its first tick":undefined} onClick={()=>onCommand("/chains/run",{chain:chain.name})}>▶ Run</button></span></div>
      {!chain.error&&(()=>{ const next=chain.segments.find(s=>!s.routeId); return next&&<p className="offlineConfirmation">Next to solve: {next.index+1}. {next.restart?"↺ ":""}{segmentLabel(next)} <button className="secondary" disabled={solver.running||running} onClick={()=>onCommand("/solver/start",{chain:chain.name,segment:next.index,...passes,repeat:false})}>Solve</button></p>; })()}
      {chain.error?<p className="errorBanner">{chain.error}</p>:segmentGroups(chain).map(g=>{ const st=groupStats(g), active=solver.running&&solver.chain===chain.name?solver.segment??-1:-1, here=g.segments.some(s=>s.index===active); return <details className="chainPart" key={g.segments[0].index}>
        <summary><span>{groupTitle(g)}{here&&<em className="solvingTag"> ● solving {active+1}</em>}</span><small>{st.range} · {st.solved}/{st.total}</small><b>{st.time!=null?`${st.time.toFixed(2)}s`:"—"}</b></summary>
        <div className="resultTable">
        {g.segments.map(s=>{ const ready=chain.segments.slice(0,s.index).every(p=>!!p.routeId), stale=!s.routeId&&s.staleRoutes>0; return <div className={`tableRow ${s.index===active?"solving":stale?"stale":""}`} title={stale?`Its route${s.staleRoutes>1?"s":""} no longer hold${s.staleRoutes>1?"":"s"} up (${s.staleRoutes} stale, kept as seeds)`:undefined} key={s.index}>
          <span>{s.index+1}. {s.restart?"↺ ":""}{segmentLabel({...s,module:null})}</span>
          <span>{s.time!=null?`${s.time.toFixed(2)}s`:"—"}</span>
          <span>{s.routeId?<button className="secondary" onClick={()=>onCommand("/routes/select",{routeId:s.routeId})}>Select</button>:stale?"stale":"none"}</span>
          <span className="buttonRow"><button className="secondary" disabled={running||!s.routeId||!ready} onClick={()=>onCommand("/chains/run",{chain:chain.name,through:s.index})}>▶ To here</button><button className="primary" disabled={solver.running||running||!ready} title={ready?`Solve offline from where the routes before it end, up to ${minutesText(passes.minutes)}`:"Solve the segments before it first"} onClick={()=>onCommand("/solver/start",{chain:chain.name,segment:s.index,...passes,repeat:false})}>Solve</button></span>
        </div>; })}
        </div>
      </details>; })}
    </div>)}
  </div>;
}
