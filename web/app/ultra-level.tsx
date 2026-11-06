import { memo, useEffect, useState } from "react";

const fy = (y: number) => -y;

export type Solid = { k: "poly" | "line" | "circle" | "capsule" | "area"; spike: boolean; p: number[]; r: number; tone?: "blue" | "orange" | null; spring?: number[] | null; when?: string | null; rings?: number[] | null };
export type Marker = { kind: string; name: string; x: number; y: number; area?: number[][]; spawn?: number[]; when?: string | null; box?: BoxInfo };
export type BoxInfo = { upgrade: string; gives: string; cost: number; currency: string; cap: number; category: BoxCategory;
  fresh: boolean; secret: boolean; tree: boolean; number?: string | null; ach?: string | null };
export type BoxCategory = "secret" | "bonusUnique" | "bonusBoost" | "achievement" | "movement" | "tree" | "unlock" | "course";
export type WorldState = { id: string; label: string; options: string[]; current: string };
export type StatePick = Record<string, string>;

export const capturedStates = (states: WorldState[] | undefined): StatePick => Object.fromEntries((states ?? []).map(s => [s.id, s.current]));
export function inState(when: string | null | undefined, pick: StatePick) {
  if (!when) return true;
  const [id, options] = when.split(":");
  return pick[id] === undefined || options.split("|").includes(pick[id]);
}

export function StatePicker({ states, pick, onPick, className }: { states: WorldState[]; pick: StatePick; onPick: (p: StatePick) => void; className?: string }) {
  return <>{states.map(s => <label key={s.id} className={className} title={`The level with ${s.label} in that state`}>
    <select aria-label={s.label} value={pick[s.id] ?? s.current} onChange={e => onPick({ ...pick, [s.id]: e.target.value })}>
      {s.options.map(o => <option key={o} value={o}>{o === "vman" ? "VMAN" : o[0].toUpperCase() + o.slice(1)}</option>)}
    </select></label>)}</>;
}

export type ArtIndex = { tile: number; scale: number; states: Record<string, Record<string, string>[]>;
  icons?: Record<string, { file: string; w: number; h: number; dx: number; dy: number }> };
export type MarkIcon = { href: string; w: number; h: number; dx: number; dy: number };
export const artIcons = (art: ArtIndex | null, base: string): Record<string, MarkIcon> =>
  Object.fromEntries(Object.entries(art?.icons ?? {}).map(([k, v]) => [k, { href: `${base}${v.file}.webp`, w: v.w, h: v.h, dx: v.dx, dy: v.dy }]));
export type ArtTile = { href: string; x: number; y: number; size: number };
export const artKey = (pick: StatePick) => Object.keys(pick).sort().map(k => `${k}:${pick[k]}`).join(",");

const ART_CACHE = new Map<string, ArtTile[]>();

export function artTiles(art: ArtIndex | null, base: string, pick: StatePick, x0: number, y0: number, x1: number, y1: number, px: number): ArtTile[] {
  if (!art) return [];
  let key = artKey(pick), levels = art.states[key];
  if (!levels) {
    const full = { ...pick };
    for (const k of Object.keys(art.states))
      for (const e of k.split(",")) { const [id, option] = e.split(":"); full[id] ??= option; }
    levels = art.states[key = artKey(full)];
  }
  if (!levels?.length) return [];
  const level = Math.max(0, Math.min(levels.length - 1, Math.floor(Math.log2(art.scale * px))));
  const size = art.tile / (art.scale / 2 ** level);
  const i0 = Math.floor(x0 / size), i1 = Math.floor(x1 / size), j0 = Math.floor(y0 / size), j1 = Math.floor(y1 / size);
  const cacheKey = `${base}${key}|${level}|${i0},${j0},${i1},${j1}`;
  let out = ART_CACHE.get(cacheKey);
  if (!out) {
    const files = levels[level];
    out = [];
    for (let i = i0; i <= i1; i++)
      for (let j = j0; j <= j1; j++) {
        const f = files[`${i},${j}`];
        if (f) out.push({ href: `${base}${f}.webp`, x: i * size, y: j * size, size });
      }
    if (ART_CACHE.size > 32) ART_CACHE.clear();
    ART_CACHE.set(cacheKey, out);
  }
  return out;
}

export const Art = memo(function Art({ tiles }: { tiles: ArtTile[] }) {
  const [back, setBack] = useState<ArtTile[]>([]);
  useEffect(() => {
    let live = true;
    Promise.all(tiles.map(t => new Promise<void>(done => { const img = new Image(); img.onload = img.onerror = () => done(); img.src = t.href; })))
      .then(() => { if (live) setBack(tiles); });
    return () => { live = false; };
  }, [tiles]);
  const key = (t: ArtTile) => t.href + t.x + "," + t.y;
  const now = new Set(tiles.map(key));
  const all = back === tiles ? tiles : [...back.filter(t => !now.has(key(t))), ...tiles];
  return <g style={{ pointerEvents: "none" }}>{all.map(t => <image key={key(t)} href={t.href} x={t.x} y={fy(t.y + t.size)} width={t.size} height={t.size} preserveAspectRatio="none"/>)}</g>;
});

export type Zip = { name: string; zip: boolean; box: number[]; path: number[][]; corridor: number[][]; when?: string | null };
export const ZIP_COLOR = "#ffb74d";

export const Zips = memo(function Zips({ zips, px }: { zips: Zip[]; px: number }) {
  const [hot, setHot] = useState(-1);
  const head = 14 * px;
  return <g>
    {zips.map((z, i) => <rect key={i} x={z.box[0]} y={fy(z.box[3])} width={z.box[2] - z.box[0]} height={z.box[3] - z.box[1]} fill={ZIP_COLOR} fillOpacity={i === hot ? .25 : .08}
      stroke={ZIP_COLOR} strokeWidth={i === hot ? 2.5 : 1.5} vectorEffect="non-scaling-stroke" style={{ cursor: "pointer" }}
      onPointerEnter={() => setHot(i)} onPointerLeave={() => setHot(h => h === i ? -1 : h)} onClick={e => { e.stopPropagation(); setHot(h => h === i ? -1 : i); }}>
      <title>{`${z.zip ? "Zip mover" : "Moving platform"}: ${z.name} · ${z.path.length - 1} leg${z.path.length === 2 ? "" : "s"}`}</title></rect>)}
    {hot >= 0 && zips[hot] && (() => {
      const z = zips[hot], w = z.box[2] - z.box[0], h = z.box[3] - z.box[1];
      return <g style={{ pointerEvents: "none" }}>
        {z.corridor.map((c, k) => <path key={k} d={polyPath(c)} fill={ZIP_COLOR} fillOpacity={.18} stroke={ZIP_COLOR} strokeOpacity={.6} strokeWidth={1} strokeDasharray="4 3" vectorEffect="non-scaling-stroke"/>)}
        {z.path.slice(1).map(([x, y], k) => <rect key={k} x={x - w / 2} y={fy(y + h / 2)} width={w} height={h} fill="none" stroke={ZIP_COLOR} strokeWidth={1.5} vectorEffect="non-scaling-stroke"/>)}
        {z.path.slice(1).map(([bx, by], k) => {
          const [ax, ay] = z.path[k], len = Math.hypot(bx - ax, by - ay) || 1, ux = (bx - ax) / len, uy = (by - ay) / len;
          return <path key={`a${k}`} d={`M${ax} ${fy(ay)}L${bx} ${fy(by)}M${bx - head * (ux + uy * .6)} ${fy(by - head * (uy - ux * .6))}L${bx} ${fy(by)}L${bx - head * (ux - uy * .6)} ${fy(by - head * (uy + ux * .6))}`}
            fill="none" stroke="#fff" strokeWidth={2} strokeLinecap="round" vectorEffect="non-scaling-stroke"/>;
        })}
      </g>;
    })()}
  </g>;
});

export const Hazards = memo(function Hazards({ paths, show }: { paths: LevelPaths; show: LevelShow }) {
  return <g style={{ pointerEvents: "none" }}>
    <path d={paths.spikes} fill={SPIKE_COLOR} fillOpacity={.3} stroke={SPIKE_COLOR} strokeWidth={1.5} vectorEffect="non-scaling-stroke"/>
    {paths.spikeAreas.map((d, i) => <path key={i} d={d} fill={SPIKE_COLOR} fillOpacity={.3} fillRule="evenodd" stroke={SPIKE_COLOR} strokeWidth={1.5} vectorEffect="non-scaling-stroke"/>)}
    <path d={paths.spikeLines} fill="none" stroke={SPIKE_COLOR} strokeWidth={2} vectorEffect="non-scaling-stroke"/>
    {(["blue", "orange"] as const).map(t => show[t] && <path key={t} d={paths.tone[t].spikes} fill={SPIKE_COLOR} fillOpacity={.2} stroke={TONE_COLORS[t]} strokeWidth={2} strokeDasharray="3 2" vectorEffect="non-scaling-stroke"/>)}
  </g>;
});

export type TreeStep = { tree: number; step: number; via: string | null; box: string | null; x: number; y: number };

export const TONE_COLORS: Record<"blue" | "orange", string> = { blue: "#42a5f5", orange: "#ffa726" };
export const GROWN_COLOR = "#c5a26b";
export const SPRING_COLOR = "#ffd54f";
export const SPIKE_COLOR = "#e5484d";
export const LEVEL_COLOR = "#8193ab";
export const BOX_COLOR = "#bcaaa4";
export type MarkerShape = "square" | "small" | "diamond" | "circle" | "star";
export const MARKERS: Record<string, { color: string; text: string; shape: MarkerShape }> = {
  start: { color: "#3ddc97", text: "Start gate", shape: "square" },
  end: { color: "#ff6b6b", text: "End gate", shape: "square" },
  exit: { color: "#90a4ae", text: "Preventative Gate", shape: "square" },
  falseEnding: { color: "#f48fb1", text: "False ending", shape: "square" },
  trueEnding: { color: "#ffe082", text: "True ending", shape: "square" },
  checkpoint: { color: "#4fc3f7", text: "Checkpoint", shape: "small" },
  teleporter: { color: "#b388ff", text: "Teleporter", shape: "diamond" },
  dashRefill: { color: "#ff80ab", text: "Dash refill", shape: "circle" },
  jumpRefill: { color: "#80cbc4", text: "Jump refill", shape: "circle" },
  fullRefill: { color: "#fff59d", text: "Full refill", shape: "circle" },
};
export const BOX_KINDS: Record<BoxCategory, { color: string; text: string; shape: MarkerShape }> = {
  secret: { color: "#ffd740", text: "Secret", shape: "star" },
  bonusUnique: { color: "#66bb6a", text: "Unlock bonus", shape: "diamond" },
  bonusBoost: { color: "#b0bec5", text: "Boost bonus", shape: "diamond" },
  achievement: { color: "#ce93d8", text: "Achievement box", shape: "star" },
  movement: { color: "#29b6f6", text: "Movement/ability upgrade", shape: "diamond" },
  tree: { color: "#c5a26b", text: "Tree upgrade", shape: "square" },
  unlock: { color: "#ff7043", text: "One-off unlock", shape: "square" },
  course: { color: "#90caf9", text: "Course upgrade", shape: "small" },
};

export const boxCost = (b: BoxInfo) => b.cost <= 0 ? "free"
  : `${b.cost >= 1e6 ? b.cost.toExponential(2).replace(/(\.\d*?)0+e/, "$1e").replace(/\.e/, "e").replace("e+", "e") : String(b.cost)} ${b.currency}`;
export function boxTitle(m: Marker) {
  const b = m.box!;
  return `${BOX_KINDS[b.category].text}: ${m.name} · gives ${b.gives} · ${b.upgrade} · ${boxCost(b)} · ${b.cap === 1 ? "one-off" : `repeatable ×${b.cap}`}`
    + `${b.secret ? ` · secret${b.number ? ` #${b.number}` : ""}` : ""}${b.ach ? ` · ${b.ach}` : ""} · ${b.fresh ? "in the fresh world" : "only later"}${b.tree ? " · tree upgrade" : ""}`;
}

const polyPath = (p: number[]) => p.reduce((d, v, i) => i % 2 ? `${d} ${fy(v)}` : `${d}${i ? "L" : "M"}${v}`, "") + "Z";

export type LevelPaths = { ground: string; lines: string; spikes: string; spikeLines: string; boxes: string; grown: string; springs: string;
  springDirs: { cx: number; cy: number; ux: number; uy: number; reach: number }[];
  tone: Record<"blue" | "orange", { ground: string; spikes: string }>; areas: string[]; spikeAreas: string[] };

function ringPaths(s: Solid) {
  let d = "", at = 0;
  for (const n of s.rings ?? [s.p.length]) { d += polyPath(s.p.slice(at, at + n)); at += n; }
  return d;
}

export function levelPaths(solids: Solid[], boxes: number[][], grown: Solid[], pick: StatePick): LevelPaths {
  const out: LevelPaths = { ground: "", lines: "", spikes: "", spikeLines: "", boxes: boxes.map(polyPath).join(""), grown: grown.map(s => polyPath(s.p)).join(""),
    springs: "", springDirs: [], tone: { blue: { ground: "", spikes: "" }, orange: { ground: "", spikes: "" } }, areas: [], spikeAreas: [] };
  for (const s of solids) {
    if (!inState(s.when, pick)) continue;
    if (s.spring) {
      const [ux, uy] = s.spring, n = s.p.length / 2;
      let cx = 0, cy = 0, reach = 0;
      for (let i = 0; i < n; i++) { cx += s.p[2 * i] / n; cy += s.p[2 * i + 1] / n; }
      for (let i = 0; i < n; i++) reach = Math.max(reach, (s.p[2 * i] - cx) * ux + (s.p[2 * i + 1] - cy) * uy);
      out.springs += polyPath(s.p);
      out.springDirs.push({ cx, cy, ux, uy, reach });
      continue;
    }
    if (s.k === "area") { (s.spike ? out.spikeAreas : out.areas).push(ringPaths(s)); continue; }
    let d: string;
    if (s.k === "circle") d = `M${s.p[0] - s.r} ${fy(s.p[1])}a${s.r} ${s.r} 0 1 0 ${2 * s.r} 0a${s.r} ${s.r} 0 1 0 ${-2 * s.r} 0Z`;
    else { d = `M${s.p[0]} ${fy(s.p[1])}`; for (let i = 2; i < s.p.length; i += 2) d += `L${s.p[i]} ${fy(s.p[i + 1])}`; }
    const closed = s.k === "poly" || s.k === "circle";
    if (s.k === "poly") d += "Z";
    if (s.tone) { if (s.spike) out.tone[s.tone].spikes += d; else out.tone[s.tone].ground += d; }
    else if (s.spike) { if (closed) out.spikes += d; else out.spikeLines += d; }
    else if (closed) out.ground += d;
    else out.lines += d;
  }
  return out;
}

export type LevelShow = { spikes: boolean; boxes: boolean; blue: boolean; orange: boolean; grown: boolean; springs: boolean };

const ARROW_PX = 6;
export const Level = memo(function Level({ paths, show, px }: { paths: LevelPaths; show: LevelShow; px: number }) {
  const arrows = !show.springs || px > ARROW_PX ? "" : paths.springDirs.map(({ cx, cy, ux, uy, reach }) => {
    const len = reach + 40, head = 16, tx = cx + ux * len, ty = cy + uy * len;
    return `M${cx} ${fy(cy)}L${tx} ${fy(ty)}M${tx - head * (ux + uy)} ${fy(ty - head * (uy - ux))}L${tx} ${fy(ty)}L${tx - head * (ux - uy)} ${fy(ty - head * (uy + ux))}`;
  }).join("");
  return <g style={{ pointerEvents: "none" }}>
    <path d={paths.ground} fill={LEVEL_COLOR} fillOpacity={.55}/>
    {paths.areas.map((d, i) => <path key={i} d={d} fill={LEVEL_COLOR} fillOpacity={.55} fillRule="evenodd"/>)}
    <path d={paths.lines} fill="none" stroke={LEVEL_COLOR} strokeWidth={1.5} vectorEffect="non-scaling-stroke"/>
    {show.spikes && <>
      <path d={paths.spikes} fill={SPIKE_COLOR} fillOpacity={.85}/>
      {paths.spikeAreas.map((d, i) => <path key={i} d={d} fill={SPIKE_COLOR} fillOpacity={.85} fillRule="evenodd"/>)}
      <path d={paths.spikeLines} fill="none" stroke={SPIKE_COLOR} strokeWidth={2} vectorEffect="non-scaling-stroke"/>
    </>}
    {(["blue", "orange"] as const).map(t => show[t] && <g key={t}>
      <path d={paths.tone[t].ground} fill={TONE_COLORS[t]} fillOpacity={.45} stroke={TONE_COLORS[t]} strokeWidth={1} vectorEffect="non-scaling-stroke"/>
      <path d={paths.tone[t].spikes} fill="none" stroke={TONE_COLORS[t]} strokeWidth={2.5} strokeDasharray="3 2" vectorEffect="non-scaling-stroke"/>
    </g>)}
    {show.boxes && <path d={paths.boxes} fill={BOX_COLOR} fillOpacity={.12} stroke={BOX_COLOR} strokeWidth={1} vectorEffect="non-scaling-stroke"/>}
    {show.grown && <path d={paths.grown} fill={GROWN_COLOR} fillOpacity={.3} stroke={GROWN_COLOR} strokeWidth={1.5} strokeDasharray="6 4" vectorEffect="non-scaling-stroke"/>}
    {show.springs && <path d={paths.springs} fill={SPRING_COLOR} fillOpacity={.8} stroke={SPRING_COLOR} strokeWidth={1.5} strokeLinejoin="round" vectorEffect="non-scaling-stroke"/>}
    <path d={arrows} fill="none" stroke={SPRING_COLOR} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" vectorEffect="non-scaling-stroke"/>
  </g>;
});

const LABEL_PX = 3;
export const Marks = memo(function Marks({ markers, steps, px, kinds, areas, checkpointAreas, showSteps, icons }: { markers: Marker[]; steps: TreeStep[]; px: number; kinds: Record<string, boolean>; areas: boolean; checkpointAreas?: boolean; showSteps: boolean; icons?: Record<string, MarkIcon> }) {
  const triggers = checkpointAreas ?? areas;
  return <g>
    {triggers && markers.map((m, i) => m.kind === "checkpoint" && m.area && <path key={`trigger${i}`} d={m.area.map(polyPath).join("")} fill={MARKERS.checkpoint.color} fillOpacity={.1}
      stroke={MARKERS.checkpoint.color} strokeWidth={1.5} strokeDasharray="5 3" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>)}
    {areas && markers.map((m, i) => m.kind !== "checkpoint" && m.kind !== "box" && m.area && kinds[m.kind] && <path key={`area${i}`} d={m.area.map(polyPath).join("")} fill={MARKERS[m.kind]?.color} fillOpacity={.1}
      stroke={MARKERS[m.kind]?.color} strokeWidth={1.5} strokeDasharray="5 3" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>)}
    {markers.map((m, i) => {
      const look = m.kind === "box" ? (m.box ? BOX_KINDS[m.box.category] : undefined) : MARKERS[m.kind];
      if (!look || !kinds[m.kind === "box" ? `box:${m.box?.category}` : m.kind]) return null;
      const s = (look.shape === "small" ? 4 : look.shape === "star" ? 10 : 7) * px, x = m.x, y = fy(m.y);
      const d = look.shape === "diamond" ? `M${x} ${y - s * 1.3}L${x + s} ${y}L${x} ${y + s * 1.3}L${x - s} ${y}Z`
        : look.shape === "circle" ? `M${x - s} ${y}a${s} ${s} 0 1 0 ${2 * s} 0a${s} ${s} 0 1 0 ${-2 * s} 0Z`
        : look.shape === "star" ? starPath(x, y, s)
        : `M${x - s} ${y - s}h${2 * s}v${2 * s}h${-2 * s}Z`;
      if (m.spawn) return <rect key={i} x={m.spawn[0]} y={fy(m.spawn[3])} width={m.spawn[2] - m.spawn[0]} height={m.spawn[3] - m.spawn[1]} fill="none"
        stroke={look.color} strokeWidth={2} vectorEffect="non-scaling-stroke" style={{ pointerEvents: "stroke" }}><title>{`${look.text}: ${m.name}`}</title></rect>;
      const icon = icons?.[m.kind];
      if (icon) return <image key={i} href={icon.href} x={m.x + icon.dx - icon.w / 2} y={fy(m.y + icon.dy + icon.h / 2)} width={icon.w} height={icon.h}>
        <title>{`${look.text}: ${m.name}`}</title></image>;
      if ((m.kind === "start" || m.kind === "end" || m.kind === "exit" || m.kind === "falseEnding" || m.kind === "trueEnding") && m.area?.length) return <path key={i} d={m.area.map(polyPath).join("")} fill={look.color} fillOpacity={.15}
        stroke={look.color} strokeWidth={2} vectorEffect="non-scaling-stroke"><title>{`${look.text}: ${m.name}`}</title></path>;
      if (m.kind === "box" && m.area) return <g key={i}>
        <path d={m.area.map(polyPath).join("")} fill={look.color} fillOpacity={.12} stroke={look.color} strokeWidth={2} vectorEffect="non-scaling-stroke">
          <title>{boxTitle(m)}</title></path>
        {px <= LABEL_PX && <text x={x} y={fy(Math.max(...m.area.flatMap(p => p.filter((_, k) => k % 2 === 1)))) - 5 * px} fill={look.color} fontSize={11 * px} textAnchor="middle"
          stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke">{`${m.name} · ${boxCost(m.box!)}`}</text>}
      </g>;
      return <g key={i}>
        <path d={d} fill={look.color} fillOpacity={look.shape === "circle" ? .8 : 1} stroke="#07111f" strokeWidth={1} vectorEffect="non-scaling-stroke">
          <title>{`${look.text}: ${m.name}`}</title></path>
        {m.kind === "box" && px <= LABEL_PX && <text x={x} y={y - s - 5 * px} fill={look.color} fontSize={11 * px} textAnchor="middle"
          stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke">{`${m.name} · ${boxCost(m.box!)}`}</text>}
      </g>;
    })}
    {showSteps && steps.map((t, i) => <text key={i} x={t.x} y={fy(t.y) + 5 * px} fill={GROWN_COLOR} fontSize={14 * px} fontWeight={700} textAnchor="middle"
      stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke">{t.step || "?"}<title>{t.step ? `Tree ${t.tree} · purchase ${t.step} grows this${t.via ? ` (bought at ${t.via})` : ""}${t.box ? `; its box: ${t.box}` : ""}`
        : `Tree ${t.tree} · no box found that grows this`}</title></text>)}
  </g>;
});

function starPath(x: number, y: number, r: number) {
  let d = "";
  for (let i = 0; i < 10; i++) {
    const a = -Math.PI / 2 + i * Math.PI / 5, k = i % 2 ? r * .45 : r;
    d += `${i ? "L" : "M"}${x + k * Math.cos(a)} ${y + k * Math.sin(a)}`;
  }
  return d + "Z";
}
