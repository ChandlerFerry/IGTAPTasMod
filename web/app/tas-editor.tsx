"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import "./tas-editor.css";
import { TONE, inputName, toneOf, type SlackInput, type SlackReply } from "./human-slack";
import { request as call } from "./studio-socket";

type Command = (path: string, body?: unknown) => Promise<boolean>;

/** One input tick (the plugin's SolverTick; defaults omitted). */
export type TasTick = { x?: number; y?: number; held?: boolean; press?: boolean; release?: boolean; dash?: boolean; dashJump?: number; springDash?: number;
  lockX?: number | null; lockY?: number | null; turnX?: number | null; turnY?: number | null; reset?: boolean; pause?: number; pauseTick?: number; pauseFrame?: number };
export type TasMarker = { tick: number; note: string };
export type TasSuggestion = { index: number; description: string; kind: string; saved: number; from: number; arrival: number; speed: number };
export type TasPlayer = { x: number; y: number; vx: number; vy: number; velX: number; velY: number; momentumX: number; momentumY: number; onGround: boolean; onWall: boolean; dashActive: boolean;
  dead: boolean; airJumpsLeft: unknown; airDashesLeft: unknown; jumpBuffer: boolean; dashBuffer: boolean; coyote: unknown; cutscene: string; originX: number; originY: number;
  courseTime?: number | null };
/** GET /state/live `tas`: the open TAS editor session (TasPlugin.Editor.cs), null when none. */
export type TasLive = { open: boolean; routeId: string; name: string; savestateId?: string; course?: number | null; capabilities?: string | null;
  phase: "starting" | "held" | "playing" | "recording" | "running"; cursor: number; physics: number; tickCount: number; baseInput: number; rerecords: number;
  version: number; canUndo: boolean; canRedo: boolean; slowdown: number; greenzone: number[]; traced: number; traceVersion: number; playUntil?: number | null; ended?: string | null; finished: boolean; endTime: number; endPhysics: number;
  player?: TasPlayer | null; suggest: { running: boolean; error?: string | null; until: number; results?: TasSuggestion[] | null; tried?: number | null };
  techs?: TasTechsLive };
/** `tas.techs`: Techs here (TasPlugin.Techs.cs), the techs whose key window works from the held input tick, per direction. */
export type TasTechsLive = { at: number; running: boolean; unavailable?: string | null; done: boolean; tried: number; error?: string | null;
  list?: { tech: string; name: string; variants: TasTechVariant[] }[] | null };
export type TasTechVariant = { dir: string; angle?: number | null; start: number; died: boolean; same?: { dir: string; angle?: number | null }[] };
export type PlacePrefill = { x?: number; y?: number; vx?: number; vy?: number; rebaseAtX?: number; rebaseAtY?: number };

type CourseLite = { id: number; token: string; bonus: boolean; categories: string[]; to?: string };
type CategoryLite = { id: string; label: string };
type RouteLite = { id: string; name: string };
type SavestateLite = { id: string; name: string };

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
const SLACK_KEYS = ["x", "y", "held", "press", "release", "dash", "dashJump", "springDash", "lockX", "lockY", "turnX", "turnY", "reset", "pause"];
const num = (v?: number | null) => v == null ? "–" : String(+v.toFixed(2));
// The More column: each extra as a badge, and the legend under the roll's header ([class, example, meaning, badge text]).
const EXTRAS: [string, string, string, (t: TasTick) => string | null][] = [
  ["djump", "dash-jump f3", "Jump on the dash's 3rd frame (a dash-jump).", t => t.dashJump ? `dash-jump f${t.dashJump}` : null],
  ["aim", "aim x0.35 y1", "The stick the game reads during the dash freeze (x, y). Without the omnidash only x counts.",
    t => t.turnX != null || t.turnY != null ? `aim x${num(t.turnX)} y${num(t.turnY)}` : null],
  ["spring", "spring dash t3", "Dash on the spring launch's 3rd tick. Spring ticks take no input, so the press is set here.",
    t => t.springDash ? `spring dash t${t.springDash}` : null],
  ["lock", "lock x0 y–", "The stick held through the dash or spring ticks that take no input. – is unset: x 0, y the next input's.",
    t => t.lockX != null || t.lockY != null ? `lock x${num(t.lockX)} y${num(t.lockY)}` : null],
  ["pause", "pause 12f", "Open the pause menu for 12 rendered frames.", t => t.pause ? `pause ${t.pause}f` : null],
  ["restart", "restart", "Quick restart.", t => t.reset ? "restart" : null],
  ["cut", "cut", "Jump release (a jump cut) on this tick.", t => t.release && t.held ? "cut" : null],
];
const fmt = (v: unknown, digits = 1) =>typeof v === "number" ? v.toFixed(digits) : String(v ?? "—");

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

// Keyboard input's game keys, by physical key (KeyboardEvent.code): arrows or WASD the stick, Space jump, X dash.
type GameKey = "left" | "right" | "up" | "down" | "jump" | "dash";
const GAME_KEYS: Record<string, GameKey> = { ArrowLeft: "left", KeyA: "left", ArrowRight: "right", KeyD: "right", ArrowUp: "up", KeyW: "up",
  ArrowDown: "down", KeyS: "down", Space: "jump", KeyX: "dash" };
const GAME_KEY_ORDER: GameKey[] = ["left", "right", "up", "down", "jump", "dash"];
const GAME_KEY_LABEL: Record<GameKey, string> = { left: "←", right: "→", up: "↑", down: "↓", jump: "Jump", dash: "Dash" };
/** The keys down (codes), and whether jump or dash went down since the last advance wrote an input (a tap between two). */
type Pad = { down: Set<string>; jumpTap: boolean; dashTap: boolean };
const emptyPad = (): Pad => ({ down: new Set(), jumpTap: false, dashTap: false });

/**
 * The input tick the keys give: the stick, jump held (fixEdges sets its press), or on the first tick of an advance a jump
 * tapped and let go since the last one (press and release), and a dash pressed since the last one.
 */
function padTick(p: Pad, first: boolean): TasTick {
  const on = new Set([...p.down].map(code => GAME_KEYS[code]));
  const x = (on.has("right") ? 1 : 0) - (on.has("left") ? 1 : 0), y = (on.has("up") ? 1 : 0) - (on.has("down") ? 1 : 0);
  const t: TasTick = {};
  if (x) t.x = x;
  if (y) t.y = y;
  if (on.has("jump")) t.held = true;
  else if (first && p.jumpTap) { t.press = true; t.release = true; }
  if (first && p.dashTap) t.dash = true;
  return t;
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
  const [trace, setTrace] = useState<{ route: string; version: number; rows: TasTrace }>({ route: "", version: -1, rows: [] });
  const [speed, setSpeed] = useState(1);
  const [recordSpeed, setRecordSpeed] = useState(4);
  const [view, setView] = useState<"grid" | "text">("grid");
  const [text, setText] = useState("");
  const [note, setNote] = useState("");
  const [goTo, setGoTo] = useState("");
  const [shareOut, setShareOut] = useState<{ code: string; name: string; fileName: string; document: unknown; text: string } | null>(null);

  useEffect(() => {
    if (loaded.route === tas.routeId && loaded.version === tas.version) return;
    let alive = true;
    call("/tas/ticks").then(r => { if (!alive) return; setTicks(r.ticks); setMarkers(r.markers); setLoaded({ route: r.routeId, version: r.version }); }).catch(() => {});
    return () => { alive = false; };
  }, [tas.routeId, tas.version, loaded]);

  // The trace grows as the session plays (only the new rows are fetched) and starts over where the inputs changed.
  useEffect(() => {
    const fresh = trace.route !== tas.routeId || trace.version !== tas.traceVersion;
    const from = fresh ? 0 : trace.rows.length;
    if (!fresh && from >= tas.traced) return;
    let alive = true;
    call("/tas/trace", { from }).then(r => {
      if (!alive) return;
      setTrace(t => ({ route: tas.routeId, version: tas.traceVersion, rows: [...(r.from === 0 || t.route !== tas.routeId ? [] : t.rows.slice(0, r.from)), ...r.trace] }));
    }).catch(() => {});
    return () => { alive = false; };
  }, [tas.routeId, tas.traced, tas.traceVersion, trace]);

  // A Human version session (`<course>_<CAT> human <time>`) shows its humanize slack report, on the rows whose inputs
  // (and every one before them) are still the human version's.
  const [slack, setSlack] = useState<SlackReply | null>(null);
  useEffect(() => {
    setSlack(null);
    const m = /^(\S+) human ([\d.]+)(?: \d+)?$/.exec(tas.name);
    if (!m) return;
    let alive = true;
    call("/enginesim/slack", { name: `${m[1]} ${m[2]}` }).then(r => { if (alive) setSlack(r); }).catch(() => {});
    return () => { alive = false; };
  }, [tas.name]);
  const slackRows = useMemo(() => {
    const at = new Map<number, SlackInput[]>(), human = slack?.ticks ?? [];
    const key = (t: object) => JSON.stringify(SLACK_KEYS.map(k => Number((t as Record<string, unknown>)[k] ?? 0)));
    let upTo = 0;
    while (upTo < ticks.length && upTo < human.length && key(ticks[upTo]) === key(human[upTo])) upTo++;
    for (const e of slack?.slack.inputs ?? [])
      for (let t = e.tick; t < Math.max(e.tick + 1, e.end) && t < upTo; t++) at.set(t, [...(at.get(t) ?? []), e]);
    return { at, upTo };
  }, [slack, ticks]);

  const busy = tas.phase === "starting" || tas.phase === "recording";
  const cmd = (path: string, body: unknown = {}) => onCommand(path, body);

  // Keyboard input: the page takes game keys (GAME_KEYS) and each advance writes what they hold into the input it plays.
  const [keyboard, setKeyboard] = useState(false);
  useEffect(() => setKeyboard(window.localStorage.getItem("tasKeyboard") === "1"), []);
  const toggleKeyboard = () => setKeyboard(on => { window.localStorage.setItem("tasKeyboard", on ? "0" : "1"); return !on; });
  const [heldKeys, setHeldKeys] = useState<string>("");
  const pad = useRef<Pad>(emptyPad());
  // When a keyboard advance was sent: the next waits for the session to move (its cursor is stale till then), 1.5 s at most.
  const pending = useRef(0);
  // Advances pressed meanwhile queue up, each with the keys as they were at its press, and go one per move.
  const queued = useRef<{ n: number; p: Pad }[]>([]);
  const advanceNow = useRef<(n: number, p: Pad) => void>(() => {});
  useEffect(() => {
    pending.current = 0;
    const next = queued.current.shift();
    if (next) advanceNow.current(next.n, next.p);
  }, [tas.physics]);

  // The game's hotkeys, here too (Shift: ten ticks). By physical key, so Shift+] is still ]; not while typing.
  const keys = useRef({ tas, busy, speed, recordSpeed, cmd, keyboard, ticks, setTicks });
  keys.current = { tas, busy, speed, recordSpeed, cmd, keyboard, ticks, setTicks };
  useEffect(() => {
    const show = () => { const on = new Set([...pad.current.down].map(code => GAME_KEYS[code])); setHeldKeys(GAME_KEY_ORDER.filter(k => on.has(k)).map(k => GAME_KEY_LABEL[k]).join(" ")); };
    /** Advance n physics ticks; with keyboard input on, the n inputs from the cursor become what the keys (p) held first. */
    const advance = async (n: number, p: Pad) => {
      const { tas, busy, cmd, keyboard, ticks, setTicks } = keys.current;
      if (busy || tas.ended) return;
      if (keyboard) {
        if (Date.now() - pending.current < 1500) { queued.current.push({ n, p }); return; }
        pending.current = Date.now();
        const at = tas.cursor, next = ticks.slice();
        next.splice(at, Math.min(n, next.length - at), ...Array.from({ length: n }, (_, i) => padTick(p, i === 0)));
        if (next[at + n]) next[at + n] = { ...next[at + n] };  // fixEdges sets its press: a copy, not the shown tick
        fixEdges(next, at, at + n - 1);
        // A tick whose buttons and stick are what it had keeps its extras (dash-jump, aims, locks...).
        const keyOf = (t?: TasTick) => t && [t.x ?? 0, t.y ?? 0, !!t.held, !!t.press, !!t.release, !!t.dash].join();
        const count = Math.min(n + 1, next.length - at), old = ticks.slice(at, at + count);
        for (let i = 0; i < old.length; i++) if (keyOf(next[at + i]) === keyOf(old[i])) next[at + i] = old[i];
        const put = next.slice(at, at + count);
        if (put.length !== old.length || put.some((t, i) => t !== old[i])) {
          setTicks(next);
          if (!await cmd("/tas/edit", { from: at, deleteCount: old.length, ticks: put })) { pending.current = 0; queued.current = []; return; }
        }
      }
      if (!await cmd("/tas/advance", { ticks: n })) { pending.current = 0; queued.current = []; }
    };
    advanceNow.current = advance;
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.altKey || e.metaKey || (e.target as HTMLElement | null)?.closest("input, textarea, select, [contenteditable]")) return;
      const { tas, busy, speed, recordSpeed, cmd, keyboard } = keys.current, n = e.shiftKey ? 10 : 1;
      const game = keyboard ? GAME_KEYS[e.code] : undefined;
      if (game) {
        // Game keys are the page's only: no scroll, no button click, no piano-roll selection.
        e.preventDefault(); e.stopPropagation();
        (document.activeElement as HTMLElement | null)?.blur();
        if (e.repeat) return;
        pad.current.down.add(e.code);
        if (game === "jump") pad.current.jumpTap = true;
        if (game === "dash") pad.current.dashTap = true;
        show();
        return;
      }
      if (e.repeat) return;
      if (e.code === "BracketRight" || (!keyboard && e.code === "ArrowRight")) {
        // The keys as they are now; a tap between two advances goes in once.
        const p = pad.current;
        advance(n, { down: new Set(p.down), jumpTap: p.jumpTap, dashTap: p.dashTap });
        p.jumpTap = p.dashTap = false;
        show();
      }
      else if (e.code === "BracketLeft" || (!keyboard && e.code === "ArrowLeft")) { if (!busy) cmd("/tas/rewind", { ticks: n }); }
      else if (e.code === "Backslash" || (!keyboard && e.code === "Space")) {
        // Space would scroll the page, or click the focused button on its keyup: blur it so the key only plays/holds.
        if (e.code === "Space") (document.activeElement as HTMLElement | null)?.blur();
        if (tas.phase === "playing") cmd("/tas/hold"); else if (!busy && !tas.ended) cmd("/tas/play", { slowdown: speed }); }
      else if (e.code === "Quote") { if (tas.phase === "recording") cmd("/tas/record/stop"); else if (!busy && !tas.ended) cmd("/tas/record", { slowdown: recordSpeed }); }
      else return;
      e.preventDefault(); e.stopPropagation();
    };
    const onKeyUp = (e: KeyboardEvent) => {
      if (!keys.current.keyboard || !GAME_KEYS[e.code]) return;
      e.preventDefault(); e.stopPropagation();
      pad.current.down.delete(e.code);
      show();
    };
    const release = () => { pad.current = emptyPad(); show(); };
    // Capture: ahead of the piano roll's own keys (arrows select rows there).
    window.addEventListener("keydown", onKey, true);
    window.addEventListener("keyup", onKeyUp, true);
    window.addEventListener("blur", release);
    return () => { window.removeEventListener("keydown", onKey, true); window.removeEventListener("keyup", onKeyUp, true); window.removeEventListener("blur", release); };
  }, []);
  useEffect(() => { pad.current = emptyPad(); setHeldKeys(""); }, [keyboard]);
  const edit = (from: number, deleteCount: number, insert: TasTick[]) => cmd("/tas/edit", { from, deleteCount, ticks: insert });
  const playTo = (until: number) => cmd("/tas/play", until > tas.cursor ? { slowdown: speed, until } : { slowdown: speed, until, from: Math.max(0, until - LEAD_IN) });
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
    if (!await cmd("/tas/close")) return;
    try {
      const r = await call("/enginesim/submit", { course, capabilities, routeId });
      setNote(r.saved ? `New record: ${r.time.toFixed(2)}s${r.previousBest != null ? ` (was ${r.previousBest.toFixed(2)})` : ""}.`
        : r.rejected ? `Rejected by the gate: ${r.rejected}`
        : r.finished ? `Finished in ${r.time.toFixed(2)}s: not faster than the record${r.previousBest != null ? ` (${r.previousBest.toFixed(2)})` : ""}${r.farmSaved ? "; saved as the farm route" : ""}.`
        : "The replay from the course start didn't finish.");
    }
    catch (e) { setNote(e instanceof Error ? e.message : String(e)); }
  };
  const status = tas.ended ? (tas.finished ? `Finished · ${tas.endTime.toFixed(3)}s · ${tas.endPhysics} physics ticks` : `Ended: ${tas.ended}`)
    : tas.phase === "starting" ? "Starting (quick restart, settling)…" : tas.phase === "recording" ? `Re-recording: play it in the game (keyboard or pad); ' or Stop keeps it, Undo brings the old inputs back`
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
        <form className="tasGoTo" onSubmit={e => { e.preventDefault(); if (goTo !== "") cmd("/tas/seek", { tick: Math.max(0, Math.min(tas.tickCount, Number(goTo))) }); }}>
          <input type="number" min={0} max={tas.tickCount} value={goTo} placeholder="input" title="Turbo to this input tick and hold there" onChange={e => setGoTo(e.target.value)}/>
          <button disabled={busy || goTo === ""}>Go</button>
        </form>
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
        <span className="tasSep"/>
        <button className={keyboard ? "active" : ""} aria-pressed={keyboard} title="Type game inputs here: hold keys, then ] writes them into the input it plays" onClick={toggleKeyboard}>⌨ Keyboard input</button>
        {keyboard && <span className={`tasPad ${heldKeys ? "on" : ""}`} title="What the next ] writes">{heldKeys || "no keys"}</span>}
      </div>
      {keyboard
        ? <p className="tasKeys">Keyboard input: hold <kbd>←</kbd><kbd>→</kbd><kbd>↑</kbd><kbd>↓</kbd> or <kbd>W</kbd><kbd>A</kbd><kbd>S</kbd><kbd>D</kbd> stick · <kbd>Space</kbd> jump · <kbd>X</kbd> dash, then <kbd>]</kbd> writes them into the input it plays and advances (<kbd>Shift</kbd> for 10). A jump or dash pressed and let go between two advances goes in too. <kbd>[</kbd> rewind · <kbd>\</kbd> play/hold · <kbd>&apos;</kbd> re-record. Held, the game waits before its next physics tick: nothing moves until you advance.</p>
        : <p className="tasKeys">Here or in game: <kbd>]</kbd> advance · <kbd>[</kbd> rewind (here also <kbd>→</kbd> <kbd>←</kbd>, <kbd>Shift</kbd> for 10) · <kbd>\</kbd> play/hold (here also <kbd>Space</kbd>) · <kbd>&apos;</kbd> re-record · <kbd>F8</kbd> close (in game). Held, the game waits before its next physics tick: nothing moves until you advance.</p>}
      {note && <div className="errorBanner tasNotice">{note}<button onClick={() => setNote("")}>×</button></div>}
    </div>

    <div className="tasGrid">
      <div className="panel tasMain">
        {view === "grid" && slack && <p className="tasHint tasSlack">Human slack ({slack.file}):
          {[TONE.exact, { color: "#ffbf5b", short: "window" }, TONE.band, TONE.free].map(t => <span key={t.short}><i style={{ background: t.color }}/>{t.short}</span>)}
          {" "}· {slack.slack.exact} exact · {slackRows.upTo === ticks.length ? "the session is the human version" : `edited at input ${slackRows.upTo}: shown above it only`}</p>}
        {view === "grid"
          ? <TasRoll ticks={ticks} markers={markers} slack={slackRows.at} trace={trace.route === tas.routeId ? trace.rows : []} cursor={tas.cursor} greenzone={tas.greenzone} base={tas.baseInput} busy={busy} follow={tas.phase === "playing" || tas.phase === "recording"}
              onPlayTo={playTo} onUndo={() => tas.canUndo && cmd("/tas/undo")} onRedo={() => tas.canRedo && cmd("/tas/redo")}
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
            <button disabled={!tas.player} title="A placed loadstate at this position and speed (Savestates tab)" onClick={() => tas.player && onPlace({ x: tas.player.x, y: tas.player.y, vx: tas.player.velX, vy: tas.player.velY })}>Place loadstate here…</button>
            <button disabled={busy} title="A savestate (player only) of this tick" onClick={() => cmd("/savestates/capture")}>Capture savestate</button>
          </div>
        </div>

        {tas.techs && <TechsHere techs={tas.techs} held={tas.phase === "held"} busy={busy} onTry={(tech, v) => cmd("/tas/techs/apply", { tech, dir: v.dir, angle: v.angle ?? null, slowdown: speed })}/>}

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
          <MarkerList markers={markers} trace={trace.rows} end={ticks.length} cursor={tas.cursor} busy={busy} onSeek={tick => cmd("/tas/seek", { tick })} onMarker={(tick, n) => cmd("/tas/marker", { tick, note: n })}/>
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

function MarkerList({ markers, trace, end, cursor, busy, onSeek, onMarker }: { markers: TasMarker[]; trace: TasTrace; end: number; cursor: number; busy: boolean;
  onSeek: (t: number) => void; onMarker: (t: number, n: string) => void }) {
  const [text, setText] = useState("");
  // Each segment's length to the next marker (or the end): input ticks, and physics ticks once it has been played.
  const length = (from: number, to: number) => {
    const a = from === 0 ? 0 : trace[from - 1]?.[0], b = trace[to - 1]?.[0];
    return `${to - from} inputs${a != null && b != null ? ` · ${b - a} ticks` : ""}`;
  };
  return <div className="tasMarkers">
    {markers.map((m, k) => <div key={m.tick} className="tasMarker"><button disabled={busy} title="Go there (the session holds before this input)" onClick={() => onSeek(m.tick)}>{m.tick}</button>
      <span>{m.note}<small>{length(m.tick, k + 1 < markers.length ? markers[k + 1].tick : end)}</small></span><button className="danger" onClick={() => onMarker(m.tick, "")}>×</button></div>)}
    {!markers.length && <p className="tasHint">Markers are save points: go to one and the session holds there, rebuilt from the start.</p>}
    <div className="tasMarkerAdd"><input value={text} placeholder={`Note at input ${cursor}`} onChange={e => setText(e.target.value)}/><button disabled={!text.trim()} onClick={() => { onMarker(cursor, text.trim()); setText(""); }}>Mark</button></div>
  </div>;
}

type Paint = { column: "x" | "y" | "jump"; from: number; to: number; value: number | boolean };
/** Per input tick as played (POST /tas/trace): [physics tick its span ends on, x, y, vx, vy]; null where not played yet. */
export type TasTrace = (number[] | null)[];
const LEAD_IN = 50;

/**
 * The piano roll (TAStudio / FCEUX TAS Editor conventions): one row per input tick, a seek column, drag-select on the
 * numbers, paint on the inputs, the physics tick each input starts on (dash and spring ticks take no input: +n), the
 * player's position and speed as played, edited cells flash.
 */
export function TasRoll({ ticks, markers, slack, trace = [], cursor = -1, greenzone = [], base = 0, busy, follow = false,
  onSeek, onEdit, onMarker, onPlayTo, onUndo, onRedo }: {
  ticks: TasTick[]; markers: TasMarker[]; slack?: Map<number, SlackInput[]>; trace?: TasTrace; cursor?: number; greenzone?: number[]; base?: number; busy: boolean; follow?: boolean;
  onSeek?: (t: number) => void; onEdit?: (from: number, deleteCount: number, insert: TasTick[]) => void; onMarker?: (t: number, n: string) => void;
  onPlayTo?: (t: number) => void; onUndo?: () => void; onRedo?: () => void }) {
  const scroller = useRef<HTMLDivElement>(null);
  const [top, setTop] = useState(0);
  const [anchor, setAnchor] = useState<number | null>(null);
  const [selection, setSelection] = useState<[number, number] | null>(null);
  const [dragSel, setDragSel] = useState(false);
  const [paint, setPaint] = useState<Paint | null>(null);
  const [wheel, setWheel] = useState<{ from: number; to: number; value: number } | null>(null);
  const [clip, setClip] = useState<TasTick[]>([]);
  const [xValue, setXValue] = useState(1);
  const [insertCount, setInsertCount] = useState(1);
  const [hot, setHot] = useState({ from: 0, to: -1, gen: 0 });
  const prev = useRef<TasTick[] | null>(null);
  const noteAt = useMemo(() => new Map(markers.map(m => [m.tick, m.note])), [markers]);
  const green = useMemo(() => new Set(greenzone), [greenzone]);
  const edit = (from: number, del: number, insert: TasTick[]) => { if (!busy) onEdit?.(from, del, insert); };

  // Flash what an edit changed (FCEUX's hot changes): the rows between the unchanged head and tail.
  useEffect(() => {
    const p = prev.current;
    prev.current = ticks;
    if (!p) return;
    const same = (a?: TasTick, b?: TasTick) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
    let head = 0;
    while (head < p.length && head < ticks.length && same(p[head], ticks[head])) head++;
    if (head === p.length && head === ticks.length) return;
    let tail = 0;
    while (tail < p.length - head && tail < ticks.length - head && same(p[p.length - 1 - tail], ticks[ticks.length - 1 - tail])) tail++;
    setHot(h => ({ from: head, to: Math.max(head, ticks.length - 1 - tail), gen: h.gen + 1 }));
  }, [ticks]);

  const sel = selection && selection[0] < ticks.length ? [selection[0], Math.min(selection[1], ticks.length - 1)] as [number, number] : null;
  const reveal = (i: number) => {
    const el = scroller.current;
    if (!el) return;
    if (i * ROW < el.scrollTop) el.scrollTop = i * ROW;
    else if ((i + 1) * ROW > el.scrollTop + el.clientHeight) el.scrollTop = (i + 1) * ROW - el.clientHeight;
  };
  useEffect(() => {
    const el = scroller.current;
    if (!el || cursor < 0) return;
    const y = cursor * ROW;
    if (follow || y < el.scrollTop || y > el.scrollTop + el.clientHeight - ROW * 2) el.scrollTop = Math.max(0, y - el.clientHeight / 2);
  }, [cursor, follow]);

  const shown = useMemo(() => {
    if (!paint && !wheel) return ticks;
    const draft = ticks.map(t => ({ ...t }));
    if (wheel) for (let i = wheel.from; i <= wheel.to && i < draft.length; i++) draft[i].x = wheel.value;
    if (!paint) return draft;
    const [a, b] = paint.from <= paint.to ? [paint.from, paint.to] : [paint.to, paint.from];
    for (let i = a; i <= b && i < draft.length; i++) {
      if (paint.column === "x") draft[i].x = paint.value as number;
      else if (paint.column === "y") draft[i].y = paint.value as number;
      else { draft[i].held = paint.value as boolean; if (!paint.value) { draft[i].press = false; draft[i].release = false; } }
    }
    if (paint.column === "jump") fixEdges(draft, a, b);
    return draft;
  }, [ticks, paint, wheel]);

  /** Replace `del` inputs at `from` with `insert`, the jump edges at both seams as a hold gives them. */
  const splice = (from: number, del: number, insert: TasTick[]) => {
    const all = [...ticks.slice(0, from), ...insert.map(t => ({ ...t })), ...ticks.slice(from + del)];
    const end = from + insert.length;
    fixEdges(all, from, from - 1);
    fixEdges(all, end, end - 1);
    edit(from, Math.min(ticks.length, from + del + 1) - from, all.slice(from, Math.min(all.length, end + 1)));
  };

  const finishPaint = () => {
    if (!paint) return;
    const [a, b] = paint.from <= paint.to ? [paint.from, paint.to] : [paint.to, paint.from];
    const end = Math.min(b + 2, shown.length);
    edit(a, end - a, shown.slice(a, end));
    setPaint(null);
  };
  useEffect(() => {
    const up = () => { finishPaint(); setDragSel(false); };
    window.addEventListener("mouseup", up);
    return () => window.removeEventListener("mouseup", up);
  });

  // The wheel on an X cell steps through a controller's values (the selection's, if it's in it); it lands once the wheel rests.
  const wheelState = useRef({ ticks, sel, wheel, busy, onEdit });
  wheelState.current = { ticks, sel, wheel, busy, onEdit };
  const wheelTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      const cell = (e.target as HTMLElement).closest<HTMLElement>("[data-x]");
      const { ticks, sel, wheel, busy } = wheelState.current;
      if (!cell || busy) return;
      e.preventDefault();
      const i = Number(cell.dataset.x);
      const [from, to] = wheel ? [wheel.from, wheel.to] : sel && i >= sel[0] && i <= sel[1] ? sel : [i, i];
      const now = wheel ? wheel.value : ticks[from]?.x ?? 0;
      const k = X_VALUES.indexOf(X_VALUES.reduce((a, b) => Math.abs(b - now) < Math.abs(a - now) ? b : a));
      const value = X_VALUES[Math.max(0, Math.min(X_VALUES.length - 1, k + (e.deltaY < 0 ? 1 : -1)))];
      setWheel({ from, to, value });
      if (wheelTimer.current) clearTimeout(wheelTimer.current);
      wheelTimer.current = setTimeout(() => {
        const w = { from, to, value }, all = wheelState.current.ticks;
        wheelState.current.onEdit?.(w.from, w.to - w.from + 1, all.slice(w.from, w.to + 1).map(t => ({ ...t, x: w.value })));
        setWheel(null);
      }, 400);
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, []);

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
    edit(i, draft.length, draft);
  };
  const toggleDash = (i: number) => {
    const t = { ...ticks[i], dash: !ticks[i].dash };
    if (!t.dash) { t.dashJump = 0; t.turnX = null; t.turnY = null; }
    edit(i, 1, [t]);
  };
  const select = (i: number, extend: boolean) => {
    i = Math.max(0, Math.min(ticks.length - 1, i));
    if (extend && anchor !== null) setSelection([Math.min(anchor, i), Math.max(anchor, i)]);
    else { setAnchor(i); setSelection([i, i]); }
    reveal(i);
  };
  const rangeEdit = (change: (t: TasTick, i: number) => TasTick, fix = false) => {
    if (!sel) return;
    const end = Math.min(sel[1] + 2, ticks.length);
    const draft = ticks.slice(sel[0], end).map((t, k) => sel[0] + k <= sel[1] ? change({ ...t }, sel[0] + k) : { ...t });
    if (fix) { const all = ticks.slice(0, sel[0]).concat(draft); fixEdges(all, sel[0], sel[1]); edit(sel[0], draft.length, all.slice(sel[0])); }
    else edit(sel[0], draft.length, draft);
  };
  const copy = () => { if (sel) setClip(ticks.slice(sel[0], sel[1] + 1).map(t => ({ ...t }))); };
  const at = () => sel ? sel[0] : Math.max(0, Math.min(cursor, ticks.length));
  const clear = () => { if (sel) rangeEdit(() => ({}), true); };
  const remove = () => { if (sel) { splice(sel[0], sel[1] - sel[0] + 1, []); setSelection(null); } };
  const insertBlank = (n: number) => splice(at(), 0, Array.from({ length: n }, () => ({})));
  const clone = () => { if (sel) splice(sel[0], 0, ticks.slice(sel[0], sel[1] + 1)); };
  const paste = (insert: boolean) => { if (!clip.length) return; const a = at(); splice(a, insert ? 0 : Math.min(clip.length, ticks.length - a), clip); };
  const mark = (i: number) => { const n = window.prompt(`Marker at input ${i}`, noteAt.get(i) ?? ""); if (n !== null) onMarker?.(i, n.trim()); };
  const markerSeek = (dir: 1 | -1) => {
    const list = markers.map(m => m.tick).filter(t => dir > 0 ? t > cursor : t < cursor);
    if (list.length) onSeek?.(dir > 0 ? Math.min(...list) : Math.max(...list));
  };

  // TAStudio / FCEUX keys, while no field has the focus.
  const keys = useRef({ sel, anchor, select, copy, clear, remove, insertBlank, clone, paste, mark, markerSeek, insertCount, onSeek, onPlayTo, onUndo, onRedo, busy, ticks });
  keys.current = { sel, anchor, select, copy, clear, remove, insertBlank, clone, paste, mark, markerSeek, insertCount, onSeek, onPlayTo, onUndo, onRedo, busy, ticks };
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.target as HTMLElement | null)?.closest("input, textarea, select, [contenteditable]")) return;
      const k = keys.current, ctrl = e.ctrlKey || e.metaKey, key = e.key.toLowerCase();
      const lead = k.sel ? (k.anchor === k.sel[0] ? k.sel[1] : k.sel[0]) : k.anchor ?? -1;
      const write = !k.busy;
      if (e.key === "ArrowDown" || e.key === "ArrowUp") k.select(lead + (e.key === "ArrowDown" ? 1 : -1), e.shiftKey);
      else if ((e.key === "PageDown" || e.key === "PageUp") && e.shiftKey) k.markerSeek(e.key === "PageDown" ? 1 : -1);
      else if (e.key === "PageDown" || e.key === "PageUp") k.select(lead + (e.key === "PageDown" ? VISIBLE : -VISIBLE), false);
      else if (e.key === "Escape") setSelection(null);
      else if (ctrl && key === "c" && k.sel) k.copy();
      else if (ctrl && key === "a" && k.ticks.length) { setAnchor(0); setSelection([0, k.ticks.length - 1]); }
      else if (!write) return;
      else if (e.key === "Enter" && k.sel) { if (e.shiftKey) k.onPlayTo?.(k.sel[0]); else k.onSeek?.(k.sel[0]); }
      else if (e.key === "Insert" && ctrl) k.clone();
      else if (e.key === "Insert") k.insertBlank(e.shiftKey ? k.insertCount : 1);
      else if (e.key === "Delete" && ctrl) k.remove();
      else if (e.key === "Delete") k.clear();
      else if (ctrl && key === "x") { k.copy(); k.remove(); }
      else if (ctrl && key === "v") k.paste(e.shiftKey);
      else if (ctrl && key === "z" && !e.shiftKey) k.onUndo?.();
      else if (ctrl && (key === "y" || key === "z")) k.onRedo?.();
      else if (!ctrl && !e.altKey && key === "m" && k.sel) k.mark(k.sel[0]);
      else return;
      e.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const physAt = (i: number) => i < base ? null : i === base ? 0 : trace[i - 1]?.[0] ?? null;
  const rows = ticks.length + 1;
  const first = Math.max(0, Math.floor(top / ROW) - 4), last = Math.min(rows, first + VISIBLE + 8);
  const cells: React.ReactElement[] = [];
  for (let i = first; i < last; i++) {
    const t = shown[i], selected = !!sel && i >= sel[0] && i <= sel[1], isHot = i >= hot.from && i <= hot.to;
    const cls = `tasRow ${i === cursor ? "cursor" : ""} ${i < cursor ? "played" : ""} ${selected ? "selected" : ""} ${i < base ? "locked" : ""} ${noteAt.has(i) ? "marked" : ""} ${isHot ? "hot" : ""}`;
    const seek = <span className={`tasSeek ${green.has(i) ? "green" : ""}`} title={green.has(i) ? "Go here (a greenzone snapshot: instant)" : "Go here: turbo to this input and hold"}
      onClick={() => !busy && onSeek?.(i)}>{i === cursor ? "▶" : ""}</span>;
    if (!t) { cells.push(<div key={i} className={`${cls} end`} style={{ top: i * ROW }}>{seek}<span className="tasNum">{i}</span><span className="tasEndCell">end · advancing past it adds neutral ticks</span></div>); continue; }
    const p0 = physAt(i), tr = trace[i], p1 = tr?.[0], span = p0 != null && p1 != null ? p1 - p0 : null;
    const on = slack?.get(i), strict = on?.reduce((a, b) => toneOf(b.class).rank > toneOf(a.class).rank ? b : a), tone = strict && toneOf(strict.class);
    const extras = EXTRAS.map(([cls, , tip, text]) => { const s = text(t); return s && <i key={cls} className={`tasBadge ${cls}`} title={tip}>{s}</i>; });
    cells.push(<div key={isHot ? `${i}-${hot.gen}` : i} className={`${cls} ${span != null && span > 1 ? "lag" : ""}`} style={{ top: i * ROW }}>
      {seek}
      <span className="tasNum" style={tone ? { boxShadow: `inset -3px 0 0 ${tone.color}` } : undefined}
        title={(tone ? `${tone.label}: ${on!.map(inputName).join(" · ")}\n` : "") + "Click or drag: select · shift: extend · double-click: go here"}
        onMouseDown={e => { if (e.button !== 0) return; select(i, e.shiftKey); setDragSel(true); }}
        onMouseEnter={() => dragSel && anchor !== null && setSelection([Math.min(anchor, i), Math.max(anchor, i)])}
        onDoubleClick={() => !busy && onSeek?.(i)}>{i}</span>
      <span data-x={i} className={`tasCell x ${t.x ? "on" : ""}`} title={"Click or drag: R, L, rest · wheel: part tilts (the selection's, if in it)"} onMouseDown={e => e.button === 0 && startPaint("x", i)} onMouseEnter={() => paint?.column === "x" && setPaint({ ...paint, to: i })}>{xText(t.x)}</span>
      <span className={`tasCell y ${t.y ? "on" : ""}`} onMouseDown={e => e.button === 0 && startPaint("y", i)} onMouseEnter={() => paint?.column === "y" && setPaint({ ...paint, to: i })}>{yText(t.y)}</span>
      <span className={`tasCell jump ${t.held ? "on" : ""} ${t.press ? "press" : ""} ${isTap(t) ? "tap" : ""}`} title={"Click or drag: hold jump · right-click: tap (press and release in one tick)"}
        onMouseDown={e => e.button === 0 && startPaint("jump", i)} onContextMenu={e => { e.preventDefault(); if (!busy) toggleTap(i); }}
        onMouseEnter={() => paint?.column === "jump" && setPaint({ ...paint, to: i })}>{isTap(t) ? "T" : t.held ? (t.press ? "J" : "│") : t.press ? "P" : ""}</span>
      <span className={`tasCell dash ${t.dash ? "on" : ""}`} title={"Click: dash press"} onClick={() => !busy && toggleDash(i)}>{t.dash ? "X" : ""}</span>
      <span className="tasExtra">{extras}</span>
      <span className="tasPhys" title={span != null && span > 1 ? `${span - 1} more physics tick${span > 2 ? "s" : ""} with no input follow (dash or spring)` : "Physics tick this input plays on"}>{p0 ?? ""}{span != null && span > 1 ? <b>+{span - 1}</b> : null}</span>
      <span className="tasVal">{tr ? tr[1].toFixed(1) : ""}</span><span className="tasVal">{tr ? tr[2].toFixed(1) : ""}</span>
      <span className="tasVal">{tr ? tr[3].toFixed(1) : ""}</span><span className="tasVal">{tr ? tr[4].toFixed(1) : ""}</span>
      <span className="tasNote" onDoubleClick={() => mark(i)}>{noteAt.get(i) ?? ""}</span>
    </div>);
  }

  return <div className="tasRollWrap">
    <details className="howTo"><summary>What the More column means</summary><ul>
      {EXTRAS.map(([cls, example, tip]) => <li key={cls}><i className={`tasBadge ${cls}`}>{example}</i> {tip}</li>)}
      <li><b>Phys +n</b>: the dash or spring ticks after this input, which take no input.</li>
    </ul></details>
    <div className="tasRollHead"><span/><span>#</span><span>X</span><span>Y</span><span>Jump</span><span>Dash</span><span>More</span><span>Phys</span><span>x</span><span>y</span><span>vx</span><span>vy</span><span>Marker (M)</span></div>
    <div className="tasRoll" ref={scroller} style={{ height: ROW * VISIBLE }} onScroll={e => setTop(e.currentTarget.scrollTop)}>
      <div style={{ height: rows * ROW, position: "relative" }}>{cells}</div>
    </div>
    <div className="tasTools">
      {sel ? <span className="tasSelLabel">Inputs {sel[0]}–{sel[1]} ({sel[1] - sel[0] + 1})</span> : <span className="tasSelLabel">Drag or shift-click input numbers to select.</span>}
      {sel && <>
        {onPlayTo && <button disabled={busy} title={`Play at the chosen speed up to input ${sel[0]} and hold (from ${LEAD_IN} inputs before it if it's behind you)  Shift+Enter`} onClick={() => onPlayTo(sel[0])}>▶ Play to {sel[0]}</button>}
        <select value={xValue} onChange={e => setXValue(Number(e.target.value))}>{X_VALUES.map(v => <option key={v} value={v}>{v === 0 ? "0 (rest)" : xText(v)}</option>)}</select>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, x: xValue }))}>Set X</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, y: 1 }))}>Y up</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, y: 0 }))}>Y rest</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, y: -1 }))}>Y down</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, held: true }), true)}>Hold jump</button>
        <button disabled={busy} onClick={() => rangeEdit(t => ({ ...t, held: false, press: false, release: false }), true)}>No jump</button>
        <button disabled={busy} title="Del" onClick={clear}>Clear</button>
        <button disabled={busy} title="Ctrl+C" onClick={copy}>Copy</button>
        <button disabled={busy || !clip.length} title="Over the selection  Ctrl+V" onClick={() => paste(false)}>Paste{clip.length ? ` (${clip.length})` : ""}</button>
        <button disabled={busy || !clip.length} title="Before the selection  Ctrl+Shift+V" onClick={() => paste(true)}>Paste insert</button>
        <button disabled={busy} title="Insert a copy before it  Ctrl+Ins" onClick={clone}>Clone</button>
        <button className="danger" disabled={busy} title="Ctrl+Del" onClick={remove}>Delete</button>
      </>}
      <>
        <span className="tasSep"/>
        <input type="number" min={1} max={500} value={insertCount} onChange={e => setInsertCount(Math.max(1, Number(e.target.value)))}/>
        <button disabled={busy} title="Neutral inputs before the selection (or the held input)  Ins: one · Shift+Ins: this many" onClick={() => insertBlank(insertCount)}>Insert blank</button>
      </>
    </div>
    <p className="tasKeys"><kbd>↑</kbd><kbd>↓</kbd> select (<kbd>Shift</kbd> extends) · <kbd>Enter</kbd> go · <kbd>Shift+Enter</kbd> play to · <kbd>Ins</kbd> insert · <kbd>Ctrl+Ins</kbd> clone · <kbd>Del</kbd> clear · <kbd>Ctrl+Del</kbd> delete · <kbd>Ctrl+X/C/V</kbd> (<kbd>Shift</kbd>: insert) · <kbd>Ctrl+Z/Y</kbd> · <kbd>M</kbd> marker · <kbd>Shift+PgUp/PgDn</kbd> markers</p>
  </div>;
}

const COMPASS = ["UL", "U", "UR", "L", "", "R", "DL", "D", "DR"];
/** Input ticks after which a tech's button is dimmed: it starts after a walk of more than half a second. */
const LATER = 25;
/** Input ticks after which a tech is hidden until "Show more": it starts more than a second away. */
const FAR = 50;
const ARROW: Record<string, string> = { UL: "↖", U: "↑", UR: "↗", L: "←", "": "•", R: "→", DL: "↙", D: "↓", DR: "↘" };
const hoverTech = (tech?: string, v?: TasTechVariant) => call("/tas/techs/hover", !tech ? {} : v ? { tech, dir: v.dir, angle: v.angle ?? null } : { tech }).catch(() => {});

/**
 * Techs here: one button per tech that works from the held tick, soonest first, with when it starts (a click tries its
 * soonest direction). Hovering it draws all its directions in game, that one bright, and opens its compass; an arrow shows
 * and tries that direction, the wheel on a diagonal steps through its steep freeze aims, and a dim arrow is a direction
 * that plays the same as another.
 */
function TechsHere({ techs, held, busy, onTry }: { techs: TasTechsLive; held: boolean; busy: boolean; onTry: (tech: string, v: TasTechVariant) => void }) {
  const [open, setOpen] = useState<string | null>(null);
  const [steep, setSteep] = useState<Record<string, number>>({});
  const [all, setAll] = useState(false);
  // The wheel steps a diagonal's aims; the page mustn't scroll under it (React's wheel listener is passive).
  const compass = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = compass.current;
    if (!el) return;
    const stop = (e: WheelEvent) => e.preventDefault();
    el.addEventListener("wheel", stop, { passive: false });
    return () => el.removeEventListener("wheel", stop);
  }, [open]);
  const found = techs.list ?? [];
  const soon = (t: { variants: TasTechVariant[] }) => Math.min(...t.variants.map(v => v.start));
  const far = found.filter(t => soon(t) > FAR).length, list = all ? found : found.filter(t => soon(t) <= FAR);
  const status = techs.unavailable ?? (!held ? "Hold on a tick: the techs that fit there appear after a second."
    : techs.error ? null : techs.running ? `Searching… ${found.length} found` : !techs.done ? "Waiting for the tick to settle…"
    : found.length ? `${found.length} techs fit here (${techs.tried} tried). Hover one to see it in game.` : `No techs fit here (${techs.tried} tried).`);
  return <div className="panel"><p className="eyebrow">TECHS HERE</p>
    {status && <p className="tasHint">{status}</p>}
    {techs.error && <p className="tasError">{techs.error}</p>}
    {held && !techs.unavailable && <div className="techsHere">
      {list.map(t => {
        const soonest = t.variants.reduce((a, b) => b.start < a.start ? b : a);
        return <div key={t.tech} className="techWrap" onMouseEnter={() => { setOpen(t.tech); hoverTech(t.tech, soonest); }} onMouseLeave={() => { setOpen(null); hoverTech(); }}>
          <button disabled={busy} className={soonest.start > LATER ? "later" : ""} title={`Try it: ${ARROW[soonest.dir]}${soonest.angle != null ? ` ${soonest.angle}°` : ""}, starting ${soonest.start} input ticks from here`}
            onClick={() => onTry(t.tech, soonest)}>{t.name}<small>{soonest.start ? `${(soonest.start * TICK).toFixed(2)} s` : "now"}</small></button>
          {open === t.tech && t.variants.some(v => v.dir) && <div className="techCompass" ref={compass}>
            {COMPASS.map(dir => {
              const here = t.variants.filter(v => v.dir === dir);
              const alias = here.length ? null : t.variants.find(v => v.same?.some(o => o.dir === dir));
              if (alias) return <button key={dir} disabled={busy} className="alias" title={`Plays the same as ${ARROW[alias.dir]}${alias.angle != null ? ` ${alias.angle}°` : ""}`}
                onMouseEnter={() => hoverTech(t.tech, alias)} onMouseLeave={() => hoverTech(t.tech, soonest)} onClick={() => onTry(t.tech, alias)}>{ARROW[dir]}</button>;
              if (!here.length) return <span key={dir} className="empty">{ARROW[dir]}</span>;
              const key = `${t.tech}|${dir}`, v = here[(steep[key] ?? 0) % here.length];
              return <button key={dir} disabled={busy} className={v.died ? "died" : ""}
                title={`${v.angle != null ? `${v.angle}° from vertical · ` : ""}starts in ${v.start} input ticks${v.died ? " · dies after it" : ""}${here.length > 1 ? " · wheel: steeper aims" : ""}`}
                onMouseEnter={() => hoverTech(t.tech, v)} onMouseLeave={() => hoverTech(t.tech, soonest)} onClick={() => onTry(t.tech, v)}
                onWheel={e => { if (here.length < 2) return; const n = ((steep[key] ?? 0) + (e.deltaY > 0 ? 1 : here.length - 1)) % here.length; setSteep({ ...steep, [key]: n }); hoverTech(t.tech, here[n]); }}>
                {ARROW[dir]}{here.length > 1 && <small>{v.angle != null ? `${Math.round(v.angle)}°` : "45°"}</small>}
              </button>;
            })}
          </div>}
        </div>;
      })}
      {far > 0 && <button className="techsMore" onClick={() => setAll(!all)}>{all ? `Hide ${far} starting over a second away` : `Show ${far} more (starting over a second away)`}</button>}
    </div>}
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
