"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import "./techs.css";
import { Art, artIcons, artTiles, Hazards, Zips, type Zip, capturedStates, GROWN_COLOR, inState, Level, levelPaths, MARKERS, Marks, SPRING_COLOR, StatePicker, TONE_COLORS, type ArtIndex, type LevelShow, type Marker, type Solid, type StatePick, type TreeStep, type WorldState } from "./ultra-level";

// Buy boxes (a marker's `box`), kept in step with level.tsx's BOX_KINDS (ultra-level.tsx is synced from that file).
export type BoxCategory = "secret" | "bonusUnique" | "bonusBoost" | "achievement" | "movement" | "tree" | "unlock" | "course";
export type BoxInfo = { upgrade: string; gives: string; cost: number; currency: string; cap: number; category: BoxCategory;
  fresh: boolean; secret: boolean; tree: boolean; number?: string | null; ach?: string | null };
type MarkerShape = "square" | "small" | "diamond" | "circle" | "star";
export const BOX_KINDS: Record<BoxCategory, { color: string; text: string; shape: MarkerShape }> = {
  secret: { color: "#ffd740", text: "Secret (a bonus's hidden reward box)", shape: "star" },
  bonusUnique: { color: "#66bb6a", text: "Bonus reward — unique unlock (good)", shape: "diamond" },
  bonusBoost: { color: "#b0bec5", text: "Bonus reward — cash/stat boost (weak)", shape: "diamond" },
  achievement: { color: "#ce93d8", text: "Achievement box (grants an achievement)", shape: "star" },
  movement: { color: "#29b6f6", text: "Movement/ability upgrade", shape: "diamond" },
  tree: { color: "#c5a26b", text: "Tree upgrade", shape: "square" },
  unlock: { color: "#ff7043", text: "One-off unlock", shape: "square" },
  course: { color: "#90caf9", text: "Course upgrade (repeatable)", shape: "small" },
};
// A box's cost as its label shows it ("300 W", "free"); big numbers in exponent form.
export const boxCost = (b: BoxInfo) => b.cost <= 0 ? "free"
  : `${b.cost >= 1e6 ? b.cost.toExponential(2).replace(/(\.\d*?)0+e/, "$1e").replace(/\.e/, "e").replace("e+", "e") : String(b.cost)} ${b.currency}`;
// What a box is, gives and costs, in one line (the hover readout; the tap card says the same).
export function boxTitle(m: { name: string; box?: BoxInfo }) {
  const b = m.box!;
  return `${BOX_KINDS[b.category].text}: ${m.name} · gives ${b.gives} · ${b.upgrade} · ${boxCost(b)} · ${b.cap === 1 ? "one-off" : `repeatable ×${b.cap}`}`
    + `${b.secret ? ` · secret${b.number ? ` #${b.number}` : ""}` : ""}${b.ach ? ` · ${b.ach}` : ""} · ${b.fresh ? "in the fresh world" : "only later"}${b.tree ? " · tree upgrade" : ""}`;
}
// The markers of the synced ultra-level carry a buy box's info; until the sync they only carry the shape here.
export type BoxMarker = Marker & { box?: BoxInfo };

export type UltraData = { world: string; bounds: number[]; player: { stand: number[]; dash: number[] }; kinds: string[];
  floors: number[][]; solids: Solid[]; boxes: number[][]; labels: { name: string; x: number; y: number }[]; launches: number;
  // The trees' ground where it grows once its tree upgrades are bought (none of it in the fresh world the rest is from),
  // and each segment's place in its tree's purchase order.
  grown?: Solid[]; treeSteps?: TreeStep[];
  markers?: Marker[]; states?: WorldState[]; zips?: Zip[];
  ultraFields: string[]; ultras: number[][]; pairFields: string[]; pairs: number[][];
  // FloatingOrigin in a fresh VMAN world: leaving this circle (two vertical lines with use2D) rebases the world.
  rebase?: { threshold: number; use2D: boolean; center: number[];
    // Each rebase on the VMAN run's best so far: [x, y, physics tick, x, y of the origin it left].
    route?: number[][] } };
type Ultra = { fromX: number; fromY: number; side: number; x: number; y: number; drop: number; hyper: number;
  hold: number; delay: number; jump: number; gap: number; jump2: number; landX: number; prevX: number; shift: number; prevY: number };
// One way an ultra goes: lastHyperY set on floor `from`, the ultra's hyper on floor `to`; the flight line a → b.
// `line`: the flight line check found it; `ultra`: the engine's biggest-drop ultra on this pair.
type Pair = { index: number; from: number; to: number; drop: number; ax: number; ay: number; bx: number; by: number; line: boolean; ultra: Ultra | null };
type Layers = { art: boolean; background: boolean; tree: boolean; shapes: boolean; grown: boolean; markers: boolean; floors: boolean; labels: boolean; rebase: boolean; next: boolean } & Record<`box:${BoxCategory}`, boolean>;
type Which = "all" | "engine" | "shift";
type Direction = "both" | "out" | "in";
type View = { x: number; y: number; w: number };

// The next rebase line, around the point of the last rebase (the new origin).
const NEXT_COLOR = "#18ffff";
// Pairs into the selected floor (where an ultra onto it can come from).
const IN_COLOR = "#80deea";

const KIND_COLORS: Record<string, string> = { metal: "#4fc3f7", moss: "#81c784", moving: "#ffb74d" };
const KIND_TEXT: Record<string, string> = { metal: "Ground (refills dashes)", moss: "Moss (refills nothing)", moving: "Moving platform (where it was)" };
// Drop colour bands (units the ultra's floor is below the floor lastHyperY came from).
const BANDS: [number, string][] = [[16, "#fff176"], [64, "#ffb74d"], [160, "#ff7043"], [400, "#e040fb"], [Infinity, "#7c4dff"]];
// Ultras from a FloatingOrigin rebase: the world moved, lastHyperY didn't, so the check passes on any floor.
const SHIFT_COLOR = "#76ff03";
// Pairs listed for the selected floor, biggest drop first.
const LIST = 24;

// Map y up (world) to SVG y down.
const fy = (y: number) => -y;
// Origin-shift colour for a pair only an origin-shift ultra makes (no flight line with a real drop).
const pairColor = (p: Pair) => !p.line && p.ultra?.shift ? SHIFT_COLOR : BANDS[BANDS.findIndex(([top]) => p.drop < top)][1];
// A rebase line: the circle of radius t around the origin (two vertical lines when only x counts).
const circle = (cx: number, cy: number, t: number, use2D: boolean) => use2D ? `M${cx - t} -1e6V1e6M${cx + t} -1e6V1e6`
  : `M${cx - t} ${fy(cy)}a${t} ${t} 0 1 0 ${2 * t} 0a${t} ${t} 0 1 0 ${-2 * t} 0Z`;
const floorY = (f: number[], x: number) => f[2] > f[0] ? f[1] + (f[3] - f[1]) * Math.min(1, Math.max(0, (x - f[0]) / (f[2] - f[0]))) : f[1];
const floorPath = (f: number[]) => `M${f[0]} ${fy(f[1])}L${f[2]} ${fy(f[3])}`;
// A pair's arrow: a curve bowing up, so pairs between the same floors stay apart and a slope's self-pair shows.
const arrowPath = (p: Pair) => {
  const mx = (p.ax + p.bx) / 2, my = Math.max(p.ay, p.by) + Math.max(24, Math.abs(p.bx - p.ax) * .25);
  return `M${p.ax} ${fy(p.ay)}Q${mx} ${fy(my)} ${p.bx} ${fy(p.by)}`;
};

export function UltraMap({ load }: { load: (refresh: boolean) => Promise<UltraData> }) {
  const [data, setData] = useState<UltraData | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState("");
  const [layers, setLayers] = useState<Layers>({ art: true, background: false, tree: true, shapes: false, grown: true, markers: true, floors: true, labels: true, rebase: true, next: true,
    // The one-off buy box kinds on (what you're looking for), the dense repeatables off (one question each).
    "box:secret": true, "box:bonusUnique": true, "box:bonusBoost": true, "box:achievement": true,
    "box:movement": true, "box:unlock": true, "box:tree": false, "box:course": false });
  // The game's art (web/public/art, tools/render-art.py); without it the level is its collision shapes.
  const [art, setArt] = useState<ArtIndex | null>(null);
  useEffect(() => { fetch("/art/index.json").then(r => r.ok ? r.json() as Promise<ArtIndex> : null).then(setArt, () => setArt(null)); }, []);
  // The world state the level is drawn in (Area 1 normal or overgrown); the capture's until picked.
  const [world, setWorld] = useState<StatePick>({});
  // Where on the first rebase line the last rebase happened (an index into `arc`), for the next line.
  const [nextAt, setNextAt] = useState(-1);
  const [which, setWhich] = useState<Which>("all");
  const [direction, setDirection] = useState<Direction>("both");
  const [view, setView] = useState<View | null>(null);
  const [cursor, setCursor] = useState<{ x: number; y: number } | null>(null);
  // The buy box tapped (what it is, gives and costs): its tooltip card where hover titles aren't a thing.
  const [boxTip, setBoxTip] = useState<BoxMarker | null>(null);
  const [hoverFloor, setHoverFloor] = useState(-1);
  const [floor, setFloor] = useState(-1);
  const [focus, setFocus] = useState<Pair | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  const drag = useRef<{ x: number; y: number; moved: boolean } | null>(null);

  const fetchData = useCallback(async (refresh: boolean) => {
    setBusy(true); setFailure("");
    try { const d = await load(refresh); setData(d); setWorld(w => ({ ...capturedStates(d.states), ...w })); setView(v => v ?? fitView(d, svg.current)); setFloor(-1); setFocus(null); }
    catch (e) { setFailure(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  }, [load]);
  useEffect(() => { fetchData(false); }, [fetchData]);

  const ultras = useMemo<Ultra[]>(() => data?.ultras.map(u => Object.fromEntries(data.ultraFields.map((f, i) => [f, u[i]])) as Ultra) ?? [], [data]);
  const pairs = useMemo<Pair[]>(() => {
    if (!data?.pairs) return [];
    const at = (name: string) => data.pairFields.indexOf(name);
    const [from, to, drop, ax, ay, bx, by, line, ultra] = ["from", "to", "drop", "ax", "ay", "bx", "by", "line", "ultra"].map(at);
    return data.pairs.map((r, index) => ({ index, from: r[from], to: r[to], drop: r[drop], ax: r[ax], ay: r[ay], bx: r[bx], by: r[by],
      line: r[line] === 1, ultra: r[ultra] >= 0 ? ultras[r[ultra]] : null }));
  }, [data, ultras]);
  const shown = useMemo(() => pairs.filter(p => which === "all" ? p.line || !p.ultra?.shift : which === "engine" ? p.ultra && !p.ultra.shift : p.ultra?.shift), [pairs, which]);
  // Per floor: the pairs leaving it and the pairs landing on it, biggest drop first.
  const byFloor = useMemo(() => {
    const n = data?.floors.length ?? 0, out: Pair[][] = Array.from({ length: n }, () => []), into: Pair[][] = Array.from({ length: n }, () => []);
    for (const p of shown) { out[p.from].push(p); into[p.to].push(p); }
    for (const list of [...out, ...into]) list.sort((a, b) => b.drop - a.drop);
    return { out, into };
  }, [shown, data]);

  const rebaseLine = useMemo(() => {
    const r = data?.rebase; if (!r || !isFinite(r.threshold)) return "";
    return circle(r.center[0], r.center[1], r.threshold, r.use2D);
  }, [data]);
  // Lines the route's later rebases crossed (the first one is the fresh world's, drawn above).
  const routeLines = useMemo(() => {
    const r = data?.rebase; if (!r || !isFinite(r.threshold)) return "";
    return (r.route ?? []).filter(p => p[3] !== r.center[0] || p[4] !== r.center[1]).map(p => circle(p[3], p[4], r.threshold, r.use2D)).join("");
  }, [data]);
  // Points of the first rebase line inside the level, every quarter degree, clockwise from the left: each is a place
  // the first rebase can happen, and the next line is the circle around it.
  const arc = useMemo(() => {
    const r = data?.rebase; if (!r || !isFinite(r.threshold) || r.use2D) return [];
    const [x0, y0, x1, y1] = data!.bounds, points: number[][] = [];
    for (let q = 0; q < 1440; q++) {
      const a = Math.PI - q * Math.PI / 720, x = r.center[0] + r.threshold * Math.cos(a), y = r.center[1] + r.threshold * Math.sin(a);
      if (x >= x0 && x <= x1 && y >= y0 && y <= y1) points.push([x, y]);
    }
    return points;
  }, [data]);
  // The point of the line nearest the run's first rebase (-1 without one); the slider starts there.
  const runAt = useMemo(() => {
    const hit = data?.rebase?.route?.[0]; if (!hit || !arc.length) return -1;
    let best = 0;
    arc.forEach((p, i) => { if (Math.hypot(p[0] - hit[0], p[1] - hit[1]) < Math.hypot(arc[best][0] - hit[0], arc[best][1] - hit[1])) best = i; });
    return best;
  }, [arc, data]);
  useEffect(() => { setNextAt(Math.max(0, runAt)); }, [runAt]);
  const next = useMemo(() => {
    const r = data?.rebase, at = arc[nextAt]; if (!r || !at) return null;
    return { at, path: circle(at[0], at[1], r.threshold, r.use2D) };
  }, [data, arc, nextAt]);

  // With nothing selected, each floor an ultra can land on is coloured by the biggest drop onto it.
  const targetPaths = useMemo(() => {
    const paths = [...BANDS.map(() => ""), ""];
    (data?.floors ?? []).forEach((f, i) => {
      const best = byFloor.into[i][0]; if (!best) return;
      paths[!best.line && best.ultra?.shift ? BANDS.length : BANDS.findIndex(([top]) => best.drop < top)] += floorPath(f);
    });
    return paths.map((d, i) => ({ d, color: i < BANDS.length ? BANDS[i][1] : SHIFT_COLOR }));
  }, [data, byFloor]);
  const floorPaths = useMemo(() => (data?.kinds ?? []).map((_, k) => (data?.floors ?? []).filter(f => f[4] === k).map(floorPath).join("")), [data]);
  const level = useMemo(() => levelPaths(data?.solids ?? [], data?.boxes ?? [], data?.grown ?? [], world), [data, world]);
  const markers = useMemo(() => (data?.markers ?? []).filter(m => inState(m.when, world)), [data, world]);
  const levelShow = useMemo<LevelShow>(() => ({ spikes: true, boxes: true, blue: true, orange: true, springs: true, grown: layers.grown }), [layers]);
  const markerKinds = useMemo(() => ({ ...Object.fromEntries(Object.keys(MARKERS).map(k => [k, layers.markers])),
    ...Object.fromEntries((Object.keys(BOX_KINDS) as BoxCategory[]).map(c => [`box:${c}`, layers[`box:${c}`]])) }), [layers]);

  const scale = view && svg.current ? svg.current.clientWidth / view.w : 1;
  const toWorld = (e: { clientX: number; clientY: number }) => {
    const el = svg.current; if (!el || !view) return { x: 0, y: 0 };
    const rect = el.getBoundingClientRect(), s = rect.width / view.w;
    return { x: view.x + (e.clientX - rect.left) / s, y: -(view.y + (e.clientY - rect.top) / s) };
  };

  useEffect(() => {
    const el = svg.current; if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      setView(v => {
        if (!v) return v;
        const rect = el.getBoundingClientRect(), s = rect.width / v.w;
        const mx = v.x + (e.clientX - rect.left) / s, my = v.y + (e.clientY - rect.top) / s, f = Math.exp(e.deltaY * 0.0015);
        return { x: mx - (mx - v.x) * f, y: my - (my - v.y) * f, w: v.w * f };
      });
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, [data]);

  // The floor under the pointer: the nearest segment within `reach`.
  const floorAt = (p: { x: number; y: number }, reach: number) => {
    let best = -1, gap = reach;
    (data?.floors ?? []).forEach((f, i) => {
      const x = Math.min(f[2], Math.max(f[0], p.x)), d = Math.hypot(x - p.x, floorY(f, x) - p.y);
      if (d < gap) { gap = d; best = i; }
    });
    return best;
  };
  const onDown = (e: React.PointerEvent<SVGSVGElement>) => { drag.current = { x: e.clientX, y: e.clientY, moved: false }; e.currentTarget.setPointerCapture(e.pointerId); };
  const onMove = (e: React.PointerEvent<SVGSVGElement>) => {
    const p = toWorld(e);
    setCursor(p);
    const d = drag.current; if (!view || !svg.current) return;
    const s = svg.current.getBoundingClientRect().width / view.w;
    if (!d) { setHoverFloor(floorAt(p, 10 / s)); return; }
    const dx = e.clientX - d.x, dy = e.clientY - d.y;
    if (Math.abs(dx) + Math.abs(dy) > 2) d.moved = true;
    setView({ ...view, x: view.x - dx / s, y: view.y - dy / s });
    drag.current = { ...d, x: e.clientX, y: e.clientY };
  };
  // A tap on a buy box shows its card (what it is, gives and costs); else it selects the floor under the pointer (a
  // tap off any floor clears the selection).
  const onUp = (e: React.PointerEvent<SVGSVGElement>) => {
    const d = drag.current; drag.current = null;
    if (d && d.moved || !view || !svg.current) return;
    const p = toWorld(e), reach = 10 / (svg.current.getBoundingClientRect().width / view.w);
    let near: BoxMarker | null = null, best = reach;
    for (const m of markers as BoxMarker[]) {
      if (m.kind !== "box" || !layers[`box:${m.box!.category}`]) continue;
      const gap = Math.hypot(m.x - p.x, m.y - p.y);
      if (gap < best) { best = gap; near = m; }
    }
    setBoxTip(near);
    if (!near) { setFloor(floorAt(p, reach)); setFocus(null); }
  };

  const download = () => {
    const el = svg.current; if (!el || !data) return;
    const copy = el.cloneNode(true) as SVGSVGElement;
    const [x0, y0, x1, y1] = data.bounds;
    copy.setAttribute("viewBox", `${x0} ${-y1} ${x1 - x0} ${y1 - y0}`);
    copy.setAttribute("width", String(Math.round((x1 - x0) / 4))); copy.setAttribute("height", String(Math.round((y1 - y0) / 4)));
    copy.setAttribute("xmlns", "http://www.w3.org/2000/svg");
    const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(copy)], { type: "image/svg+xml" }));
    const a = document.createElement("a"); a.href = url; a.download = "ultra-map.svg"; a.click();
    URL.revokeObjectURL(url);
  };

  const toggle = (key: keyof Layers) => setLayers(l => ({ ...l, [key]: !l[key] }));
  const px = 1 / scale;
  const artShown = !!art && layers.art;
  const viewH = view && svg.current ? view.w * svg.current.clientHeight / svg.current.clientWidth : 0;
  const tiles = view && artShown ? artTiles(art, "/art/", world, view.x, -(view.y + viewH), view.x + view.w, -view.y, px) : [];
  // The trees' own tile set ("tree:grown", tools/render-art.py) over the level: off in the fresh world, so it's drawn
  // under the tree layer.
  const treeTiles = view && artShown && layers.tree ? artTiles(art, "/art/", { ...world, tree: "grown" }, view.x, -(view.y + viewH), view.x + view.w, -view.y, px) : [];
  // The decoration (backdrops, pillars, plants: nothing solid), under the level, off by default.
  const bgTiles = view && artShown && layers.background ? artTiles(art, "/art/", { ...world, bg: "on" }, view.x, -(view.y + viewH), view.x + view.w, -view.y, px) : [];
  const f = data && floor >= 0 ? data.floors[floor] : null;
  const out = floor >= 0 && direction !== "in" ? byFloor.out[floor] : [];
  const into = floor >= 0 && direction !== "out" ? byFloor.into[floor] : [];
  const listed = [...out.map(p => ({ p, dir: "out" as const })), ...into.map(p => ({ p, dir: "in" as const }))].sort((a, b) => b.p.drop - a.p.drop);
  const hover = data && hoverFloor >= 0 && hoverFloor !== floor ? data.floors[hoverFloor] : null;
  const sel = focus?.ultra ?? null;
  // The focused pair's engine ultra, as its moves: launch → first hyper's jump (chained only) → touchdown → ultra jump.
  const legs = !sel ? [] : [
    ...(sel.hyper === 2 ? [{ a: [sel.fromX, sel.fromY], b: [sel.prevX, sel.prevY], label: "hop + wavedash", color: "#ffffff", dash: true }] : []),
    { a: sel.hyper === 2 ? [sel.prevX, sel.prevY] : [sel.fromX, sel.fromY], b: [sel.landX, sel.y], label: sel.hyper === 2 ? "jump, dash ↘ in the air" : "hop, dash ↘", color: "#ffffff", dash: false },
    { a: [sel.landX, sel.y], b: [sel.x, sel.y], label: "slide, ULTRA jump", color: "#ffd54f", dash: false },
  ];
  const where = (fl: number[]) => `${fl[0].toFixed(0)}…${fl[2].toFixed(0)} at ${fl[1] === fl[3] ? fl[1].toFixed(0) : `${fl[1].toFixed(0)}→${fl[3].toFixed(0)}`}`;
  const counts = (i: number) => `${byFloor.out[i].length} out${byFloor.out[i][0] ? ` (max ${byFloor.out[i][0].drop.toFixed(0)} u)` : ""} · ${byFloor.into[i].length} in${byFloor.into[i][0] ? ` (max ${byFloor.into[i][0].drop.toFixed(0)} u)` : ""}`;
  return <div className="panel offlinePanel solveMap"><div className="panelTitle"><div><p className="eyebrow">WHERE THE GAME&apos;S ULTRA WORKS</p><h2>Ultra map</h2></div>
    <div className="buttonRow">
      <button className="secondary" disabled={!data} onClick={() => data && setView(fitView(data, svg.current))}>Fit</button>
      <button className="secondary" disabled={!data} onClick={download} title="The whole map as one SVG file">Download SVG</button>
      <button className="secondary" disabled={busy} onClick={() => fetchData(true)} title="Recompute from the VMAN trace's world (minutes)">{busy ? "Working…" : "Recompute"}</button></div></div>
    <p className="lede">Every hyper floor pair in the world, with its drop. Click a floor for details.</p>
    <details className="howTo"><summary>How it works</summary><ol>
      <li>Ultra: a hyper whose dash follows the momentum before it.</li>
      <li>Its jump lands more than 4 u below <code>lastHyperY</code>.</li>
      <li>Success gives back all of that momentum.</li>
      <li><code>lastHyperY</code> = the last hyper&apos;s height, or the last grounded floor.</li>
      <li>So an ultra joins two floors: it leaves one, lands lower.</li>
      <li>Origin shift: the world moved, <code>lastHyperY</code> didn&apos;t; any drop passes.</li>
    </ol></details>
    <details className="howTo"><summary>Legend notes</summary><ul>
      <li>Tree ground appears once its tree upgrades are bought.</li>
      <li>Numbers are the purchase order; Plant Tree is 1.</li>
      <li>Hover a number to see the box that grows it.</li>
      <li>The fresh world has none: no floors or pairs there.</li>
    </ul></details>
    {failure && <p className="mapFailure">{failure}</p>}
    {data && !data.pairs && <p className="mapFailure">This result predates the floor pairs: Recompute.</p>}
    <div className="mapLayers">
      {([["art", "Game art"], ["background", "Background art"], ["tree", "Tree (grown)"], ["shapes", "Collision shapes"], ["grown", "Tree ground + purchase order"], ["markers", "Gates, checkpoints, teleporters, refills"], ["floors", "Hyper floors"], ["rebase", "Origin-shift line"], ["next", "Next shift line (slider)"], ["labels", "Course + bonus labels"],
        ...Object.entries(BOX_KINDS).map(([k, m]) => [`box:${k}`, `Buy box: ${m.text}`] as [keyof Layers, string])] as [keyof Layers, string][]).filter(([key]) => art || (key !== "art" && key !== "background" && key !== "tree" && key !== "shapes")).map(([key, label]) =>
        <label key={key} className="passLoop"><input type="checkbox" checked={layers[key]} onChange={() => toggle(key)}/>{label}</label>)}
      {data?.states && <StatePicker states={data.states} pick={world} onPick={setWorld} className="passLoop"/>}
      <label className="passLoop">Pairs <select value={which} onChange={e => { setWhich(e.target.value as Which); setFocus(null); }}>
        <option value="all">every height drop</option><option value="engine">engine-confirmed drops</option><option value="shift">origin-shift ultras</option></select></label>
      <label className="passLoop">Selected floor <select value={direction} onChange={e => setDirection(e.target.value as Direction)}>
        <option value="both">from and to it</option><option value="out">ultras from it</option><option value="in">ultras onto it</option></select></label>
      {data && <span className="hint">{shown.length.toLocaleString()}/{pairs.length.toLocaleString()} pairs · {data.floors.length.toLocaleString()} floors · {ultras.length.toLocaleString()} ultras · {data.world}</span>}
    </div>
    {layers.next && arc.length > 0 && <div className="mapLayers">
      <label className="passLoop" style={{ flex: 1 }}>First shift at
        <input type="range" min={0} max={arc.length - 1} value={Math.max(0, nextAt)} onChange={e => setNextAt(Number(e.target.value))} style={{ flex: 1, minWidth: 240 }}/></label>
      {next && <span className="hint"><span className="mapSwatch" style={{ background: NEXT_COLOR }}/>shift at {next.at[0].toFixed(0)}, {next.at[1].toFixed(0)} → the next line
        {runAt >= 0 && <> · <button className="secondary" disabled={nextAt === runAt} onClick={() => setNextAt(runAt)}>Back to the run&apos;s shift</button></>}</span>}
    </div>}
    <div className="mapLayers">
      {data?.kinds.map(k => <span key={k} className="hint"><span className="mapSwatch" style={{ background: KIND_COLORS[k] }}/>{KIND_TEXT[k] ?? k}</span>)}
      <span className="hint"><span className="mapSwatch" style={{ background: "#bcaaa4" }}/>Upgrade box (ground rays hit it like a floor)</span>
      <span className="hint"><span className="mapSwatch" style={{ background: GROWN_COLOR }}/>Tree ground (appears after tree upgrades)</span>
      {(["blue", "orange"] as const).map(t => <span key={t} className="hint"><span className="mapSwatch" style={{ background: TONE_COLORS[t] }}/>{t === "blue" ? "Blue" : "Orange"} blocks + spikes (dashed = spikes; only active colour is solid)</span>)}
      <span className="hint"><span className="mapSwatch" style={{ background: SPRING_COLOR }}/>Spring: trigger box; arrow shows launch direction</span>
      {[...Object.values(MARKERS), ...Object.values(BOX_KINDS)].map(m => <span key={m.text} className="hint">{m.shape === "star" ? <span style={{ color: m.color }}>★ </span> : <span className="mapSwatch" style={{ background: m.color, borderRadius: m.shape === "circle" ? 8 : 2 }}/>}{m.text}</span>)}
      <span className="hint"><span className="mapSwatch" style={{ background: "transparent", border: `2px solid ${MARKERS.checkpoint.color}` }}/>Checkpoint spawn box (where a respawn puts the player)</span>
      <span className="hint"><span className="mapSwatch" style={{ background: "transparent", border: `2px dashed ${MARKERS.checkpoint.color}` }}/>Checkpoint trigger area (touching it sets the spawn)</span>
      {BANDS.map(([top, color], i) => <span key={i} className="hint"><span className="mapSwatch" style={{ background: color, borderRadius: 8 }}/>Drop {i === 0 ? "4" : BANDS[i - 1][0]}{top === Infinity ? "+" : `–${top}`} u</span>)}
      <span className="hint"><span className="mapSwatch" style={{ background: SHIFT_COLOR, borderRadius: 8 }}/>Origin shift (any drop, even flat)</span>
      <span className="hint"><span className="mapSwatch" style={{ background: IN_COLOR }}/>Onto the selected floor</span>
      {data?.rebase && <span className="hint"><span className="mapSwatch" style={{ background: `repeating-linear-gradient(90deg, ${SHIFT_COLOR} 0 5px, transparent 5px 9px)` }}/>First rebase line ({data.rebase.threshold} from the origin)</span>}
    </div>
    <svg ref={svg} className="mapCanvas" viewBox={view && svg.current ? `${view.x} ${view.y} ${view.w} ${view.w * svg.current.clientHeight / svg.current.clientWidth}` : "0 0 1 1"}
      onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerLeave={() => { setCursor(null); setHoverFloor(-1); }}>
      <rect x={-1e6} y={-1e6} width={2e6} height={2e6} fill="#07111f"/>
      <Art tiles={bgTiles}/>
      <Art tiles={tiles}/>
      <Art tiles={treeTiles}/>
      {(!artShown || layers.shapes) && <Level paths={level} show={levelShow} px={px}/>}
      {artShown && !layers.shapes && <Hazards paths={level} show={levelShow}/>}
      {layers.floors && data?.kinds.map((k, i) => <path key={k} d={floorPaths[i]} fill="none" stroke={KIND_COLORS[k] ?? "#fff"} strokeWidth={2} strokeOpacity={f ? .35 : 1} vectorEffect="non-scaling-stroke"/>)}
      {layers.rebase && <path d={rebaseLine} fill="none" stroke={SHIFT_COLOR} strokeOpacity={.7} strokeWidth={1.5} strokeDasharray="10 8" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>}
      {layers.rebase && <path d={routeLines} fill="none" stroke={SHIFT_COLOR} strokeOpacity={.4} strokeWidth={1} strokeDasharray="4 8" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>}
      {!f && targetPaths.map(p => <path key={p.color} d={p.d} fill="none" stroke={p.color} strokeWidth={6} strokeLinecap="round" vectorEffect="non-scaling-stroke"/>)}
      <defs>
        {[...BANDS.map(([, c]) => c), SHIFT_COLOR, IN_COLOR, "#ffffff", "#ffd54f"].map(c => <marker key={c} id={`ultra-arrow-${c.slice(1)}`} viewBox="0 0 10 10" refX={9} refY={5} markerUnits="userSpaceOnUse"
          markerWidth={12 * px} markerHeight={12 * px} orient="auto"><path d="M0 0L10 5L0 10Z" fill={c}/></marker>)}
      </defs>
      {f && data && <g style={{ pointerEvents: "none" }}>
        {out.map(p => <path key={`t${p.index}`} d={floorPath(data.floors[p.to])} stroke={pairColor(p)} strokeWidth={6} strokeLinecap="round" vectorEffect="non-scaling-stroke"/>)}
        {into.map(p => <path key={`s${p.index}`} d={floorPath(data.floors[p.from])} stroke={IN_COLOR} strokeWidth={6} strokeLinecap="round" vectorEffect="non-scaling-stroke"/>)}
        {out.map(p => <path key={`o${p.index}`} d={arrowPath(p)} fill="none" stroke={pairColor(p)} strokeOpacity={focus && focus !== p ? .25 : .8} strokeWidth={focus === p ? 3 : 1.25}
          strokeDasharray={p.line ? undefined : "5 4"} vectorEffect="non-scaling-stroke" markerEnd={`url(#ultra-arrow-${pairColor(p).slice(1)})`}/>)}
        {into.map(p => <path key={`i${p.index}`} d={arrowPath(p)} fill="none" stroke={IN_COLOR} strokeOpacity={focus && focus !== p ? .2 : .7} strokeWidth={focus === p ? 3 : 1.25}
          strokeDasharray={p.line ? "2 3" : "5 4"} vectorEffect="non-scaling-stroke" markerEnd={`url(#ultra-arrow-${IN_COLOR.slice(1)})`}/>)}
        <path d={floorPath(f)} stroke="#ffffff" strokeWidth={8} strokeLinecap="round" vectorEffect="non-scaling-stroke"/>
      </g>}
      {hover && <path d={floorPath(hover)} stroke="#ffffff" strokeOpacity={.6} strokeWidth={8} strokeLinecap="round" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>}
      {layers.next && next && <g style={{ pointerEvents: "none" }}>
        <path d={next.path} fill="none" stroke={NEXT_COLOR} strokeOpacity={.75} strokeWidth={1.5} strokeDasharray="10 8" vectorEffect="non-scaling-stroke"/>
        <path d={`M${next.at[0]} ${fy(next.at[1])}h0`} stroke={NEXT_COLOR} strokeWidth={12} strokeLinecap="round" vectorEffect="non-scaling-stroke"/>
      </g>}
      {sel && <g style={{ pointerEvents: "none" }}>
        {legs.map((l, i) => <g key={i}>
          <path d={`M${l.a[0]} ${fy(l.a[1])}L${l.b[0]} ${fy(l.b[1])}`} stroke={l.color} strokeWidth={i === legs.length - 1 ? 3 : 2} strokeDasharray={l.dash ? "6 4" : undefined}
            vectorEffect="non-scaling-stroke" markerEnd={`url(#ultra-arrow-${l.color.slice(1)})`}/>
          <text x={(l.a[0] + l.b[0]) / 2} y={i % 2 ? fy(Math.min(l.a[1], l.b[1])) + 22 * px : fy(Math.max(l.a[1], l.b[1])) - 12 * px} fill={l.color} fontSize={12 * px} textAnchor="middle" stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke">{l.label}</text>
        </g>)}
      </g>}
      {focus && <text x={(focus.ax + focus.bx) / 2} y={fy(Math.max(focus.ay, focus.by) + Math.max(24, Math.abs(focus.bx - focus.ax) * .25) / 2) - 8 * px} fill="#ffffff" fontSize={13 * px} textAnchor="middle"
        stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke" style={{ pointerEvents: "none" }}>↓ {focus.drop.toFixed(1)} u</text>}
      {layers.labels && data?.labels.map(l => <text key={l.name} x={l.x} y={fy(l.y) - 30 * px} fill="#e8eef8" fontSize={14 * px} textAnchor="middle" style={{ pointerEvents: "none" }}>{l.name}</text>)}
      <Zips zips={(data?.zips ?? []).filter(z => inState(z.when, world))} px={px}/>
      <Marks icons={artIcons(art, "/art/")} markers={markers} steps={data?.treeSteps ?? []} px={px} kinds={markerKinds} areas={layers.markers} showSteps={layers.grown}/>
    </svg>
    {boxTip?.box && <div className="mapReadout"><div><strong>{boxTip.name}</strong> · {BOX_KINDS[boxTip.box.category].text}
      <button className="secondary" onClick={() => setBoxTip(null)}>×</button></div>
      <div>gives {boxTip.box.gives} · {boxTip.box.upgrade} · {boxCost(boxTip.box)} · {boxTip.box.cap === 1 ? "one-off" : `repeatable ×${boxTip.box.cap}`}</div>
      <div>{boxTip.box.secret ? `secret${boxTip.box.number ? ` #${boxTip.box.number}` : ""} · ` : ""}{boxTip.box.ach ? `${boxTip.box.ach} · ` : ""}{boxTip.box.tree ? "tree upgrade · " : ""}{boxTip.box.fresh ? "in the fresh world" : "only later"}{boxTip.when ? ` · only in ${boxTip.when}` : ""}</div></div>}
    <div className="mapReadout">{cursor ? <>world {cursor.x.toFixed(1)}, {cursor.y.toFixed(1)}</> : "Point at the map for coordinates."}
      {hover && hoverFloor >= 0 && <div>floor {where(hover)}</div>}
      {hover && hoverFloor >= 0 && <div>{counts(hoverFloor)}</div>}</div>
    {f && <div className="mapReadout"><strong>Floor {where(f)}</strong>
      <div>{counts(floor)}{listed.length > LIST && <> · top {LIST} shown</>}</div>
      <div className="techSeenList" style={{ marginTop: 8 }}>
        {listed.slice(0, LIST).map(({ p, dir }) => {
          const other = data!.floors[dir === "out" ? p.to : p.from];
          return <button key={`${dir}${p.index}`} className="seenChip" style={{ borderColor: dir === "out" ? pairColor(p) : IN_COLOR, background: focus === p ? "#1d3a5c" : undefined }}
            title={p.line ? "A flight line clears the level" : "Only the engine found this one"} onClick={() => setFocus(focus === p ? null : p)}>
            {dir === "out" ? "to" : "from"} {where(other)} · ↓ {p.drop.toFixed(0)} u{p.ultra ? " · engine ✓" : ""}{p.from === p.to ? " · downhill" : ""}</button>;
        })}
        {listed.length === 0 && <span className="muted">No ultra pairs {direction === "out" ? "from" : direction === "in" ? "onto" : "from or onto"} this floor.</span>}
      </div></div>}
    {sel && <div className="mapReadout"><strong>Engine ultra at {sel.x.toFixed(1)}, {sel.y.toFixed(1)}</strong>
      <div>{sel.shift ? <>origin shift · real drop {sel.drop.toFixed(1)} u</> : <>drop {sel.drop.toFixed(1)} u</>} · hyper {sel.hyper} of the chain</div>
      <div>launch {sel.fromX.toFixed(1)}, {sel.fromY.toFixed(1)} going {sel.side > 0 ? "right" : "left"}</div>
      <div>second dash landed at {sel.landX.toFixed(1)}</div>
      <div>inputs (ticks): stand 3, hop, dash {sel.side > 0 ? "↘" : "↙"} +{sel.delay}</div>
      <div>jump on dash frame {sel.jump}, hold {sel.hold}</div>
      {sel.gap > 0 && <div>second dash +{sel.gap}, jump on dash frame {sel.jump2}</div>}</div>}
  </div>;
}

// A view as wide as the element's aspect allows, so the viewBox is never letterboxed and screen ↔ world is one scale.
function fitView(d: UltraData, el: SVGSVGElement | null): View {
  const [x0, y0, x1, y1] = d.bounds, aspect = el && el.clientWidth ? el.clientHeight / el.clientWidth : .65;
  const w = Math.max(x1 - x0, (y1 - y0) / aspect);
  return { x: (x0 + x1) / 2 - w / 2, y: -(y0 + y1) / 2 - w * aspect / 2, w };
}
