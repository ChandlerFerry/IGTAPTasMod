"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import "./tas-editor.css";

const API = "http://127.0.0.1:17426/api";

type Command = (path: string, body?: unknown) => Promise<boolean>;

/** One input tick (the plugin's SolverTick; defaults omitted). */
export type TasTick = { x?: number; y?: number; held?: boolean; press?: boolean; release?: boolean; dash?: boolean; dashJump?: number; springDash?: number;
  lockX?: number | null; lockY?: number | null; turnX?: number | null; turnY?: number | null; reset?: boolean; pause?: number; pauseTick?: number; pauseFrame?: number };
export type TasMarker = { tick: number; note: string };
export type TasSuggestion = { index: number; description: string; kind: string; saved: number; from: number; arrival: number; speed: number };
export type TasPlayer = { x: number; y: number; vx: number; vy: number; momentumX: number; momentumY: number; onGround: boolean; onWall: boolean; dashActive: boolean;
  dead: boolean; airJumpsLeft: unknown; airDashesLeft: unknown; jumpBuffer: boolean; dashBuffer: boolean; coyote: unknown; cutscene: string; originX: number; originY: number;
  courseTime?: number | null };
/** GET /state/live `tas`: the open TAS editor session (TasPlugin.Editor.cs), null when none. */
export type TasLive = { open: boolean; routeId: string; name: string; savestateId?: string; course?: number | null; capabilities?: string | null;
  phase: "starting" | "held" | "playing" | "recording" | "running"; cursor: number; physics: number; tickCount: number; baseInput: number; rerecords: number;
  version: number; canUndo: boolean; canRedo: boolean; slowdown: number; greenzone: number[]; ended?: string | null; finished: boolean; endTime: number; endPhysics: number;
  player?: TasPlayer | null; suggest: { running: boolean; error?: string | null; until: number; results?: TasSuggestion[] | null; tried?: number | null } };
export type PlacePrefill = { x?: number; y?: number; vx?: number; vy?: number; rebaseAtX?: number; rebaseAtY?: number };

type CourseLite = { id: number; token: string; bonus: boolean; categories: string[]; to?: string };
type CategoryLite = { id: string; label: string };
type RouteLite = { id: string; name: string };
type SavestateLite = { id: string; name: string };

async function call(path: string, body?: unknown) {
  const response = await fetch(`${API}${path}`, { method: body === undefined ? "GET" : "POST", headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (!response.ok || data.error) throw new Error(data.error || `Request failed (${response.status})`);
  return data;
}

// A controller's stick (docs/human-input.md): x 0, ±1 or a part tilt 0.3..0.8; y 0 or ±1.
const PARTIALS = [0.3, 0.35, 0.4, 0.45, 0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8];
const X_VALUES = [-1, ...PARTIALS.map(v => -v).reverse(), 0, ...PARTIALS, 1];
const SPEEDS: [number, string][] = [[1, "1x"], [2, "½x"], [4, "¼x"], [10, "⅒x"]];
const ROW = 22, VISIBLE = 28, TICK = 0.02;

const xText = (x = 0) => x === 0 ? "" : (x > 0 ? "R" : "L") + (Math.abs(x) === 1 ? "" : Math.abs(x).toFixed(2).replace(/^0/, ""));
const yText = (y = 0) => y > 0 ? "U" : y < 0 ? "D" : "";
const nextX = (x = 0) => x === 0 ? 1 : x === 1 ? -1 : 0;
const nextY = (y = 0) => y === 0 ? 1 : y === 1 ? -1 : 0;
const isTap = (t: TasTick) => !t.held && !!t.press && !!t.release;
const fmt = (v: unknown, digits = 1) => typeof v === "number" ? v.toFixed(digits) : String(v ?? "—");

/**
 * The jump flags from `from` to `to` (and the tick after) as a hold's edges give them: a hold presses where it starts and
 * not after; outside a hold only a tap presses. The tick after the range only gets its press set by the edge.
 */
function fixEdges(ticks: TasTick[], from: number, to: number) {
  for (let i = from; i <= Math.min(to + 1, ticks.length - 1); i++) {
    const t = ticks[i], before = i > 0 && !!ticks[i - 1].held;
    if (t.held) t.press = !before;
    else if (i <= to && !isTap(t)) { t.press = false; t.release = false; }
  }
}

export function TasEditor({ tas, routes, courses, categories, savestates, onCommand, onImprove, onPlace }: {
  tas?: TasLive | null; routes: RouteLite[]; courses: CourseLite[]; categories: CategoryLite[]; savestates: SavestateLite[];
  onCommand: Command; onImprove: (course: number, capabilities: string) => void; onPlace: (p: PlacePrefill) => void }) {
  if (!tas) return <TasStart routes={routes} courses={courses} categories={categories} savestates={savestates} onCommand={onCommand}/>;
  return <TasSession tas={tas} courses={courses} categories={categories} onCommand={onCommand} onImprove={onImprove} onPlace={onPlace}/>;
}

function TasStart({ routes, courses, categories, savestates, onCommand }: { routes: RouteLite[]; courses: CourseLite[]; categories: CategoryLite[];
  savestates: SavestateLite[]; onCommand: Command }) {
  const playable = courses.filter(c => c.categories.length > 0).sort((a, b) => Number(a.bonus) - Number(b.bonus) || a.id - b.id);
  const [course, setCourse] = useState(0);
  const [caps, setCaps] = useState("");
  const [name, setName] = useState("");
  const [savestate, setSavestate] = useState("");
  const [routeId, setRouteId] = useState("");
  const [shared, setShared] = useState("");
  const [note, setNote] = useState("");
  const chosen = playable.find(c => c.id === course) ?? playable[0];
  const capsOf = chosen ? categories.filter(c => chosen.categories.includes(c.id)) : [];
  const cap = capsOf.find(c => c.id === caps) ?? capsOf[0];
  const label = (c: CourseLite) => c.to ? `${c.token}` : c.bonus ? `Bonus ${c.token.replace(/^bonus/i, "")}` : `Course ${c.id}`;
  const importShared = async () => {
    try { const r = await call("/tas/share/import", { code: shared }); setNote(`Imported “${r.name}”.${r.warning ? " " + r.warning : ""}`); setShared(""); await onCommand("/tas/open", { routeId: r.routeId }); }
    catch (e) { setNote(e instanceof Error ? e.message : String(e)); }
  };
  return <div className="panel tasStart">
    <div className="panelTitle"><div><p className="eyebrow">MANUAL TAS</p><h2>Start a TAS session</h2></div><span className="hint">Frame advance, rewind and tick editing on the live game</span></div>
    <p className="lede">A session is a route played at 1x that holds between physics ticks. Record what you believe is perfect, send it to the solver, open its best here, perfect it, repeat.</p>
    <div className="tasStartGrid">
      <section><h3>New on a course</h3>
        <label className="field"><span>Course</span><select value={chosen?.id ?? 0} onChange={e => setCourse(Number(e.target.value))}>{playable.map(c => <option key={c.id} value={c.id}>{label(c)}</option>)}</select></label>
        <label className="field"><span>Category</span><select value={cap?.id ?? ""} onChange={e => setCaps(e.target.value)}>{capsOf.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}</select></label>
        <label className="field"><span>Name</span><input value={name} placeholder="manual" onChange={e => setName(e.target.value)}/></label>
        <div className="buttonRow"><button className="primary" disabled={!chosen || !cap} onClick={() => onCommand("/tas/new", { course: chosen?.id, capabilities: cap?.id, name })}>New session</button>
          <button disabled={!chosen || !cap} title="The newest offline best of this course and category, from the solver" onClick={() => onCommand("/tas/from-best", { course: chosen?.id, capabilities: cap?.id })}>Open solver best</button>
          <button className="humanButton" disabled={!chosen || !cap} title="Its human version: same time, easier inputs" onClick={() => onCommand("/tas/from-best", { course: chosen?.id, capabilities: cap?.id, human: true })}>Human version</button></div>
      </section>
      <section><h3>New from a savestate</h3>
        <label className="field"><span>Savestate</span><select value={savestate} onChange={e => setSavestate(e.target.value)}><option value="">Pick one…</option>{savestates.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
        <p className="tasHint">Free-form: never a record, no offline suggestions. Place one anywhere in the Savestates tab (floating-origin tests).</p>
        <button className="primary" disabled={!savestate} onClick={() => onCommand("/tas/new", { savestateId: savestate, name })}>New session</button>
      </section>
      <section><h3>Open a route</h3>
        <label className="field"><span>Route</span><select value={routeId} onChange={e => setRouteId(e.target.value)}><option value="">Pick one…</option>{routes.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}</select></label>
        <p className="tasHint">A record opens as an editable copy: records only come from the gate.</p>
        <button className="primary" disabled={!routeId} onClick={() => onCommand("/tas/open", { routeId })}>Open</button>
      </section>
      <section><h3>Import a shared route</h3>
        <textarea className="tasShareInput" value={shared} placeholder="IGTAS1:… share code, shared JSON, or TAS text" onChange={e => setShared(e.target.value)}/>
        <button className="primary" disabled={!shared.trim()} onClick={importShared}>Import and open</button>
        {note && <p className="tasHint">{note}</p>}
      </section>
    </div>
  </div>;
}

function TasSession({ tas, courses, categories, onCommand, onImprove, onPlace }: { tas: TasLive; courses: CourseLite[]; categories: CategoryLite[];
  onCommand: Command; onImprove: (course: number, capabilities: string) => void; onPlace: (p: PlacePrefill) => void }) {
  const [ticks, setTicks] = useState<TasTick[]>([]);
  const [markers, setMarkers] = useState<TasMarker[]>([]);
  const [loaded, setLoaded] = useState({ route: "", version: -1 });
  const [speed, setSpeed] = useState(1);
  const [recordSpeed, setRecordSpeed] = useState(4);
  const [view, setView] = useState<"grid" | "text">("grid");
  const [text, setText] = useState("");
  const [note, setNote] = useState("");
  const [shareOut, setShareOut] = useState<{ code: string; name: string; fileName: string; document: unknown; text: string } | null>(null);

  useEffect(() => {
    if (loaded.route === tas.routeId && loaded.version === tas.version) return;
    let alive = true;
    call("/tas/ticks").then(r => { if (!alive) return; setTicks(r.ticks); setMarkers(r.markers); setLoaded({ route: r.routeId, version: r.version }); }).catch(() => {});
    return () => { alive = false; };
  }, [tas.routeId, tas.version, loaded]);

  const busy = tas.phase === "starting" || tas.phase === "recording";
  const cmd = (path: string, body: unknown = {}) => onCommand(path, body);
  const edit = (from: number, deleteCount: number, insert: TasTick[]) => cmd("/tas/edit", { from, deleteCount, ticks: insert });
  const category = categories.find(c => c.id === tas.capabilities);
  const course = courses.find(c => c.id === tas.course);
  const openText = async () => { try { setText((await call("/tas/text")).text); setView("text"); } catch (e) { setNote(String(e)); } };
  const share = async () => { try { setShareOut(await call("/tas/share/export", { routeId: tas.routeId })); } catch (e) { setNote(String(e)); } };
  const download = (name: string, content: string, type: string) => {
    const url = URL.createObjectURL(new Blob([content], { type }));
    const a = document.createElement("a"); a.href = url; a.download = name; a.click(); URL.revokeObjectURL(url);
  };
  const judge = async () => {
    if (!tas.course || !tas.capabilities) return;
    if (!window.confirm("Judging replays the session from the course start through the gate. It closes the session first. Judge now?")) return;
    const { routeId, course, capabilities } = tas;
    if (await cmd("/tas/close")) await cmd("/enginesim/submit", { course, capabilities, routeId });
  };
  const status = tas.ended ? (tas.finished ? `Finished · ${tas.endTime.toFixed(3)}s · ${tas.endPhysics} physics ticks` : `Ended: ${tas.ended}`)
    : tas.phase === "starting" ? "Starting (quick restart, settling)…" : tas.phase === "recording" ? "Re-recording from the controller…"
    : tas.phase === "playing" ? "Playing" : tas.phase === "running" ? "Stepping…" : "Held";

  return <div className="tasEditor">
    <div className="panel tasHead">
      <div className="panelTitle"><div><p className="eyebrow">MANUAL TAS{category ? ` · ${course ? (course.bonus ? course.token : course.to ? course.token : `Course ${course.id}`) : ""} ${category.label}` : tas.savestateId ? " · SAVESTATE START" : ""}</p><h2>{tas.name}</h2></div>
        <div className="buttonRow"><button onClick={() => cmd("/tas/close")}>Close session</button></div></div>
      <div className="tasStatus">
        <span className={`tasPhase ${tas.phase} ${tas.ended ? (tas.finished ? "finished" : "ended") : ""}`}>{status}</span>
        <span>input <b>{tas.cursor}</b> / {tas.tickCount}</span><span>physics <b>{tas.physics}</b> ({(tas.physics * TICK).toFixed(2)}s)</span>
        {tas.player?.courseTime != null && <span>timer <b>{tas.player.courseTime.toFixed(2)}</b></span>}
        <span>rerecords <b>{tas.rerecords}</b></span>
      </div>
      <div className="tasTransport">
        <button disabled={busy} title="To the start (input 0)" onClick={() => cmd("/tas/seek", { tick: 0 })}>⏮</button>
        <button disabled={busy} title="Back 10 physics ticks" onClick={() => cmd("/tas/rewind", { ticks: 10 })}>◀◀</button>
        <button disabled={busy} title="Back one physics tick  [" onClick={() => cmd("/tas/rewind", { ticks: 1 })}>◀ 1</button>
        <button disabled={busy || !!tas.ended} title="One physics tick  ]" onClick={() => cmd("/tas/advance", { ticks: 1 })}>1 ▶</button>
        <button disabled={busy || !!tas.ended} title="Ten physics ticks" onClick={() => cmd("/tas/advance", { ticks: 10 })}>▶▶</button>
        {tas.phase === "playing" ? <button className="primary" onClick={() => cmd("/tas/hold")}>❚❚ Hold</button>
          : <button className="primary" disabled={busy || !!tas.ended} title="Play to the end of the inputs  \" onClick={() => cmd("/tas/play", { slowdown: speed })}>▶ Play</button>}
        <select value={speed} onChange={e => setSpeed(Number(e.target.value))} title="Play speed">{SPEEDS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select>
        <span className="tasSep"/>
        {tas.phase === "recording" ? <button className="record" onClick={() => cmd("/tas/record/stop")}>■ Stop re-record</button>
          : <button className="record" disabled={busy || !!tas.ended} title="Drop the inputs from here and record the controller  '" onClick={() => cmd("/tas/record", { slowdown: recordSpeed })}>● Re-record</button>}
        <select value={recordSpeed} onChange={e => setRecordSpeed(Number(e.target.value))} title="Re-record speed">{SPEEDS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select>
        <span className="tasSep"/>
        <button disabled={!tas.canUndo || busy} onClick={() => cmd("/tas/undo")}>Undo</button>
        <button disabled={!tas.canRedo || busy} onClick={() => cmd("/tas/redo")}>Redo</button>
        <span className="tasSep"/>
        <button className={view === "grid" ? "active" : ""} onClick={() => setView("grid")}>Grid</button>
        <button className={view === "text" ? "active" : ""} onClick={openText}>Text</button>
      </div>
      <p className="tasKeys">In game: <kbd>]</kbd> advance · <kbd>[</kbd> rewind · <kbd>\</kbd> play/hold · <kbd>&apos;</kbd> re-record · <kbd>F8</kbd> close. Held, the game waits before its next physics tick: nothing moves until you advance.</p>
      {note && <div className="errorBanner">{note}<button onClick={() => setNote("")}>×</button></div>}
    </div>

    <div className="tasGrid">
      <div className="panel tasMain">
        {view === "grid"
          ? <TasRoll ticks={ticks} markers={markers} cursor={tas.cursor} greenzone={tas.greenzone} base={tas.baseInput} busy={busy} follow={tas.phase === "playing" || tas.phase === "recording"}
              onSeek={tick => cmd("/tas/seek", { tick })} onEdit={edit} onMarker={(tick, n) => cmd("/tas/marker", { tick, note: n })}/>
          : <div className="tasText">
              <p className="tasHint">One line per input run: <code>ticks,inputs</code>. <code>R L</code> (<code>R0.55</code> part tilt) <code>U D</code> stick, <code>J</code> jump held (presses where it starts), <code>H</code> held without that press, <code>P</code> press, <code>C</code> release, <code>X</code> dash (<code>X+3</code>, <code>X(0.35:1)</code>), <code>S3</code>, <code>K(x:y)</code>, <code>Z12@0.3</code> pause, <code>Q</code> restart, <code>@ note</code> marker. See docs/tas-markup.md.</p>
              <textarea value={text} spellCheck={false} onChange={e => setText(e.target.value)}/>
              <div className="buttonRow"><button className="primary" disabled={busy} onClick={async () => { if (await cmd("/tas/text", { text })) openText(); }}>Apply text</button>
                <button onClick={openText}>Reload</button><button onClick={() => download(`${tas.name}.tas`, text, "text/plain")}>Download .tas</button></div>
            </div>}
      </div>

      <div className="tasSide">
        <div className="panel"><p className="eyebrow">HELD TICK</p>
          {tas.player ? <dl className="tasState">
            <dt>Position</dt><dd>{fmt(tas.player.x)}, {fmt(tas.player.y)}</dd>
            <dt>Velocity</dt><dd>{fmt(tas.player.vx)}, {fmt(tas.player.vy)}</dd>
            <dt>Momentum</dt><dd>{fmt(tas.player.momentumX)}, {fmt(tas.player.momentumY)}</dd>
            <dt>Contact</dt><dd>{tas.player.dead ? "dead" : [tas.player.onGround && "ground", tas.player.onWall && "wall", tas.player.dashActive && "dashing"].filter(Boolean).join(" · ") || "air"}</dd>
            <dt>Left</dt><dd>{String(tas.player.airJumpsLeft)} jumps · {String(tas.player.airDashesLeft)} dashes</dd>
            <dt>Buffers</dt><dd>{[tas.player.jumpBuffer && "jump", tas.player.dashBuffer && "dash"].filter(Boolean).join(" · ") || "—"} · coyote {fmt(tas.player.coyote, 0)}</dd>
            <dt>Cutscene</dt><dd>{tas.player.cutscene}</dd>
            <dt>Origin</dt><dd>{fmt(tas.player.originX, 0)}, {fmt(tas.player.originY, 0)}</dd>
          </dl> : <p className="tasHint">No player in the world.</p>}
          <div className="buttonRow">
            <button disabled={!tas.player} title="A placed loadstate at this position and speed (Savestates tab)" onClick={() => tas.player && onPlace({ x: tas.player.x, y: tas.player.y, vx: tas.player.vx, vy: tas.player.vy })}>Place loadstate here…</button>
            <button disabled={busy} title="A savestate (player only) of this tick" onClick={() => cmd("/savestates/capture")}>Capture savestate</button>
          </div>
        </div>

        <div className="panel"><p className="eyebrow">OPTIMIZE SO FAR</p>
          <p className="tasHint">Offline (enginesim): nudges each press, release, dash and stick change before input {tas.cursor} and lists the ones that reach where you are now sooner.</p>
          <button className="primary" disabled={tas.suggest.running || busy || !tas.course} title={tas.course ? "" : "Course and category sessions only"} onClick={() => cmd("/tas/suggest")}>{tas.suggest.running ? "Searching…" : "Suggest improvements"}</button>
          {tas.suggest.error && <p className="tasError">{tas.suggest.error}</p>}
          {tas.suggest.results && <div className="tasSuggestions">
            <p className="tasHint">{tas.suggest.results.length ? `${tas.suggest.results.length} of ${tas.suggest.tried ?? "?"} edits reach input ${tas.suggest.until}'s spot sooner:` : `None of ${tas.suggest.tried ?? "?"} edits reach input ${tas.suggest.until}'s spot sooner.`}</p>
            {tas.suggest.results.map(s => <div key={s.index} className="tasSuggestion">
              <b>−{s.saved}</b><span>{s.description}<small>{s.kind} · from input {s.from} · speed {s.speed.toFixed(0)}</small></span>
              <button disabled={busy} onClick={() => cmd("/tas/suggest/apply", { index: s.index })}>Apply</button>
            </div>)}
          </div>}
        </div>

        {tas.course != null && tas.capabilities && <div className="panel"><p className="eyebrow">SOLVER LOOP</p>
          <p className="tasHint">This session is a seed of {category?.label} Improve. Let the solver search from it, open its best, perfect it here, repeat.</p>
          <div className="buttonRow tasWrap">
            <button onClick={() => onImprove(tas.course as number, tas.capabilities as string)}>Improve in Optimize</button>
            <button onClick={() => cmd("/tas/from-best", { course: tas.course, capabilities: tas.capabilities })}>Open solver best</button>
            <button className="humanButton" onClick={() => cmd("/tas/from-best", { course: tas.course, capabilities: tas.capabilities, human: true })}>Human version</button>
            <button className="primary" disabled={!tas.finished} title={tas.finished ? "Replay through the gate; a faster time becomes the record" : "Play the session to the finish first"} onClick={judge}>Judge as record</button>
          </div>
        </div>}

        <div className="panel"><p className="eyebrow">MARKERS</p>
          <MarkerList markers={markers} cursor={tas.cursor} busy={busy} onSeek={tick => cmd("/tas/seek", { tick })} onMarker={(tick, n) => cmd("/tas/marker", { tick, note: n })}/>
        </div>

        <div className="panel"><p className="eyebrow">SHARE</p>
          <div className="buttonRow tasWrap"><button onClick={share}>Share this route</button></div>
          {shareOut && <div className="tasShare">
            <textarea readOnly value={shareOut.code} onFocus={e => e.currentTarget.select()}/>
            <div className="buttonRow tasWrap">
              <button onClick={() => navigator.clipboard?.writeText(shareOut.code)}>Copy code</button>
              <button onClick={() => download(`${shareOut.fileName}.igtas.json`, JSON.stringify(shareOut.document, null, 2), "application/json")}>Download .igtas.json</button>
              <button onClick={() => download(`${shareOut.fileName}.tas`, shareOut.text, "text/plain")}>Download .tas</button>
            </div>
          </div>}
        </div>
      </div>
    </div>
  </div>;
}

function MarkerList({ markers, cursor, busy, onSeek, onMarker }: { markers: TasMarker[]; cursor: number; busy: boolean; onSeek: (t: number) => void; onMarker: (t: number, n: string) => void }) {
  const [text, setText] = useState("");
  return <div className="tasMarkers">
    {markers.map(m => <div key={m.tick} className="tasMarker"><button disabled={busy} title="Go there (the session holds before this input)" onClick={() => onSeek(m.tick)}>{m.tick}</button><span>{m.note}</span><button className="danger" onClick={() => onMarker(m.tick, "")}>×</button></div>)}
    {!markers.length && <p className="tasHint">Markers are save points: go to one and the session holds there, rebuilt from the start.</p>}
    <div className="tasMarkerAdd"><input value={text} placeholder={`Note at input ${cursor}`} onChange={e => setText(e.target.value)}/><button disabled={!text.trim()} onClick={() => { onMarker(cursor, text.trim()); setText(""); }}>Mark</button></div>
  </div>;
}

type Paint = { column: "x" | "y" | "jump"; from: number; to: number; value: number | boolean };

function TasRoll({ ticks, markers, cursor, greenzone, base, busy, follow, onSeek, onEdit, onMarker }: { ticks: TasTick[]; markers: TasMarker[]; cursor: number; greenzone: number[];
  base: number; busy: boolean; follow: boolean; onSeek: (t: number) => void; onEdit: (from: number, deleteCount: number, insert: TasTick[]) => void; onMarker: (t: number, n: string) => void }) {
  const scroller = useRef<HTMLDivElement>(null);
  const [top, setTop] = useState(0);
  const [anchor, setAnchor] = useState<number | null>(null);
  const [selection, setSelection] = useState<[number, number] | null>(null);
  const [paint, setPaint] = useState<Paint | null>(null);
  const [clip, setClip] = useState<TasTick[]>([]);
  const [xValue, setXValue] = useState(1);
  const [insertCount, setInsertCount] = useState(1);
  const noteAt = useMemo(() => new Map(markers.map(m => [m.tick, m.note])), [markers]);
  const green = useMemo(() => new Set(greenzone), [greenzone]);

  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const y = cursor * ROW;
    if (follow || y < el.scrollTop || y > el.scrollTop + el.clientHeight - ROW * 2) el.scrollTop = Math.max(0, y - el.clientHeight / 2);
  }, [cursor, follow]);

  const shown = useMemo(() => {
    if (!paint) return ticks;
    const draft = ticks.map(t => ({ ...t }));
    const [a, b] = paint.from <= paint.to ? [paint.from, paint.to] : [paint.to, paint.from];
    for (let i = a; i <= b && i < draft.length; i++) {
      if (paint.column === "x") draft[i].x = paint.value as number;
      else if (paint.column === "y") draft[i].y = paint.value as number;
      else { draft[i].held = paint.value as boolean; if (!paint.value) { draft[i].press = false; draft[i].release = false; } }
    }
    if (paint.column === "jump") fixEdges(draft, a, b);
    return draft;
  }, [ticks, paint]);

  const finishPaint = () => {
    if (!paint) return;
    const [a, b] = paint.from <= paint.to ? [paint.from, paint.to] : [paint.to, paint.from];
    const end = Math.min(b + 2, shown.length);
    onEdit(a, end - a, shown.slice(a, end));
    setPaint(null);
  };
  useEffect(() => {
    const up = () => finishPaint();
    window.addEventListener("mouseup", up);
    return () => window.removeEventListener("mouseup", up);
  });

  const startPaint = (column: Paint["column"], i: number) => {
    if (busy || i >= ticks.length) return;
    const t = ticks[i];
    const value = column === "x" ? nextX(t.x) : column === "y" ? nextY(t.y) : !t.held;
    setPaint({ column, from: i, to: i, value });
  };
  const toggleTap = (i: number) => {
    const draft = ticks.slice(i, Math.min(i + 2, ticks.length)).map(t => ({ ...t }));
    const t = draft[0], tap = !isTap(t);
    t.held = false; t.press = tap; t.release = tap;
    if (draft[1]?.held) draft[1].press = true;
    onEdit(i, draft.length, draft);
  };
  const toggleDash = (i: number) => {
    const t = { ...ticks[i], dash: !ticks[i].dash };
    if (!t.dash) { t.dashJump = 0; t.turnX = null; t.turnY = null; }
    onEdit(i, 1, [t]);
  };
  const clickRow = (i: number, e: React.MouseEvent) => {
    if (e.shiftKey && anchor !== null) { setSelection([Math.min(anchor, i), Math.max(anchor, i)]); return; }
    if (e.ctrlKey || e.metaKey) { setAnchor(i); setSelection([i, i]); return; }
    setAnchor(i);
    setSelection(null);
    if (!busy) onSeek(i);
  };
  const sel = selection && selection[0] < ticks.length ? [selection[0], Math.min(selection[1], ticks.length - 1)] as [number, number] : null;
  const rangeEdit = (change: (t: TasTick, i: number) => TasTick, fix = false) => {
    if (!sel) return;
    const end = Math.min(sel[1] + 2, ticks.length);
    const draft = ticks.slice(sel[0], end).map((t, k) => sel[0] + k <= sel[1] ? change({ ...t }, sel[0] + k) : { ...t });
    if (fix) { const all = ticks.slice(0, sel[0]).concat(draft); fixEdges(all, sel[0], sel[1]); onEdit(sel[0], draft.length, all.slice(sel[0])); }
    else onEdit(sel[0], draft.length, draft);
  };

  const rows = ticks.length + 1;
  const first = Math.max(0, Math.floor(top / ROW) - 4), last = Math.min(rows, first + VISIBLE + 8);
  const cells: React.ReactElement[] = [];
  for (let i = first; i < last; i++) {
    const t = shown[i], selected = !!sel && i >= sel[0] && i <= sel[1];
    const cls = `tasRow ${i === cursor ? "cursor" : ""} ${i < cursor ? "played" : ""} ${selected ? "selected" : ""} ${i < base ? "locked" : ""}`;
    if (!t) { cells.push(<div key={i} className={`${cls} end`} style={{ top: i * ROW }} onClick={e => clickRow(i, e)}><span className="tasNum">{i}</span><span className="tasEndCell">end · advancing past it adds neutral ticks</span></div>); continue; }
    const extras = [t.dashJump ? `+${t.dashJump}` : "", t.turnX != null || t.turnY != null ? `↻${t.turnX ?? ""}:${t.turnY ?? ""}` : "", t.springDash ? `S${t.springDash}` : "",
      t.lockX != null || t.lockY != null ? `K${t.lockX ?? ""}:${t.lockY ?? ""}` : "", t.pause ? `Z${t.pause}` : "", t.reset ? "Q" : "", t.release && t.held ? "C" : ""].filter(Boolean).join(" ");
    cells.push(<div key={i} className={cls} style={{ top: i * ROW }}>
      <span className={`tasNum ${green.has(i) ? "green" : ""}`} title={green.has(i) ? "Greenzone snapshot: rewinds from here are instant" : "Click: go here · shift-click: select · ctrl-click: select one"} onClick={e => clickRow(i, e)}>{i}</span>
      <span className={`tasCell x ${t.x ? "on" : ""}`} onMouseDown={() => startPaint("x", i)} onMouseEnter={() => paint?.column === "x" && setPaint({ ...paint, to: i })}>{xText(t.x)}</span>
      <span className={`tasCell y ${t.y ? "on" : ""}`} onMouseDown={() => startPaint("y", i)} onMouseEnter={() => paint?.column === "y" && setPaint({ ...paint, to: i })}>{yText(t.y)}</span>
      <span className={`tasCell jump ${t.held ? "on" : ""} ${t.press ? "press" : ""} ${isTap(t) ? "tap" : ""}`} title="Click or drag: hold jump · right-click: tap (press and release in one tick)"
        onMouseDown={e => e.button === 0 && startPaint("jump", i)} onContextMenu={e => { e.preventDefault(); if (!busy) toggleTap(i); }}
        onMouseEnter={() => paint?.column === "jump" && setPaint({ ...paint, to: i })}>{isTap(t) ? "T" : t.held ? (t.press ? "J" : "│") : t.press ? "P" : ""}</span>
      <span className={`tasCell dash ${t.dash ? "on" : ""}`} title="Click: dash press" onClick={() => !busy && toggleDash(i)}>{t.dash ? "X" : ""}</span>
      <span className="tasExtra">{extras}</span>
      <span className="tasNote" onDoubleClick={() => { const n = window.prompt(`Marker at input ${i}`, noteAt.get(i) ?? ""); if (n !== null) onMarker(i, n.trim()); }}>{noteAt.get(i) ?? ""}</span>
    </div>);
  }

  return <div className="tasRollWrap">
    <div className="tasRollHead"><span>#</span><span>X</span><span>Y</span><span>Jump</span><span>Dash</span><span>More</span><span>Marker (double-click)</span></div>
    <div className="tasRoll" ref={scroller} style={{ height: ROW * VISIBLE }} onScroll={e => setTop(e.currentTarget.scrollTop)}>
      <div style={{ height: rows * ROW, position: "relative" }}>{cells}</div>
    </div>
    <div className="tasTools">
      {sel ? <>
        <span className="tasSelLabel">Inputs {sel[0]}–{sel[1]} ({sel[1] - sel[0] + 1})</span>
        <select value={xValue} onChange={e => setXValue(Number(e.target.value))}>{X_VALUES.map(v => <option key={v} value={v}>{v === 0 ? "0 (rest)" : xText(v)}</option>)}</select>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, x: xValue }))}>Set X</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, y: 1 }))}>Y up</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, y: 0 }))}>Y rest</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, y: -1 }))}>Y down</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, held: true }), true)}>Hold jump</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, held: false, press: false, release: false }), true)}>No jump</button>
        <button disabled={busy} onClick={() => rangeEdit(() => ({}), true)}>Clear</button>
        <button disabled={busy} onClick={() => setClip(ticks.slice(sel[0], sel[1] + 1).map(t => ({ ...t })))}>Copy</button>
        <button disabled={busy || !clip.length} title="Insert the copied inputs before the selection" onClick={() => onEdit(sel[0], 0, clip)}>Paste {clip.length ? `(${clip.length})` : ""}</button>
        <button className="danger" disabled={busy} onClick={() => { onEdit(sel[0], sel[1] - sel[0] + 1, []); setSelection(null); }}>Delete</button>
      </> : <span className="tasSelLabel">Shift-click input numbers to select a range.</span>}
      <span className="tasSep"/>
      <input type="number" min={1} max={500} value={insertCount} onChange={e => setInsertCount(Math.max(1, Number(e.target.value)))}/>
      <button disabled={busy} title="Neutral inputs before the selection (or the held input)" onClick={() => onEdit(sel ? sel[0] : Math.min(cursor, ticks.length), 0, Array.from({ length: insertCount }, () => ({})))}>Insert blank</button>
    </div>
  </div>;
}

/** The Savestates tab's place form: a loadstate anywhere (/api/savestates/place), with an optional world rebase (floating-origin tests). */
export function PlaceLoadstate({ savestates, prefill, onCommand }: { savestates: SavestateLite[]; prefill: PlacePrefill | null; onCommand: Command }) {
  const [from, setFrom] = useState("");
  const [name, setName] = useState("placed");
  const [v, setV] = useState<Record<string, string>>({});
  const [facing, setFacing] = useState("");
  useEffect(() => { if (prefill) setV(Object.fromEntries(Object.entries(prefill).map(([k, x]) => [k, x == null ? "" : String(Math.round(x * 100) / 100)]))); }, [prefill]);
  const num = (k: string) => v[k] !== undefined && v[k].trim() !== "" && !Number.isNaN(Number(v[k])) ? Number(v[k]) : undefined;
  const field = (k: string, label: string, title?: string) => <label className="field" title={title}><span>{label}</span><input value={v[k] ?? ""} placeholder="keep" onChange={e => setV({ ...v, [k]: e.target.value })}/></label>;
  const rebase = num("rebaseAtX") !== undefined && num("rebaseAtY") !== undefined;
  return <div className="tasPlace">
    <p className="eyebrow">PLACE A LOADSTATE</p>
    <p className="tasHint">A copy of the player now (or of a savestate) with any of these replaced; x, y are world coordinates (the overlay&apos;s POS). Named lab/…: a start for experiments, never a record&apos;s. Rebase: after the restore the world is moved around that world point as FloatingOrigin would with the player there (it must be within its threshold of the player), so the next rebase happens where you test it.</p>
    <div className="tasPlaceGrid">
      <label className="field"><span>From</span><select value={from} onChange={e => setFrom(e.target.value)}><option value="">The player now</option>{savestates.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
      <label className="field"><span>Name</span><input value={name} onChange={e => setName(e.target.value)}/></label>
      {field("x", "x")}{field("y", "y")}{field("vx", "vx")}{field("vy", "vy")}
      <label className="field"><span>Facing</span><select value={facing} onChange={e => setFacing(e.target.value)}><option value="">keep</option><option value="1">right</option><option value="-1">left</option></select></label>
      {field("rebaseAtX", "Rebase at x", "World point to rebase around")}{field("rebaseAtY", "Rebase at y")}
    </div>
    <div className="buttonRow"><button className="primary" onClick={() => onCommand("/savestates/place", { from, name, x: num("x"), y: num("y"), vx: num("vx"), vy: num("vy"),
      facing: facing ? Number(facing) : undefined, rebaseAtX: rebase ? num("rebaseAtX") : undefined, rebaseAtY: rebase ? num("rebaseAtY") : undefined })}>Place savestate</button></div>
  </div>;
}
