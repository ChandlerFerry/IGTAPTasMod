// The level under the Ultra map: solids (springs among them), both colours of coloured blocks, other world states'
// ground (Area 1 overgrown), the trees' ground with its purchase order, and markers (gates, checkpoints, teleporters,
// refill orbs, secrets). The same file is the public viewer's src/level.tsx (github.com/ChandlerFerry/igtap-ultra-map)
// but for `fy`, which that one imports from its shifts.ts; keep the two in step.

import { memo } from "react";

// Map y up (world) to SVG y down.
const fy = (y: number) => -y;

// `spring`: a spring's trigger box, launching along this unit vector (its transform.up). `off`: in another world state
// than the export's (off in every zone of it: Area 1 overgrown's moss, spikes and zip movers), outlines only.
export type Solid = { k: "poly" | "line" | "circle" | "capsule"; spike: boolean; p: number[]; r: number; tone?: "blue" | "orange" | null; spring?: number[] | null; off?: boolean | null };
// `area`: a checkpoint's trigger (touching it sets where a death or quick restart respawns) or a secret's box (touching
// it buys it; a bonus's finish), polygons of x, y pairs.
export type Marker = { kind: string; name: string; x: number; y: number; area?: number[][] };
// One tree segment: the fewest tree purchases that grow it (Plant Tree is 1; 0: no box grows it), the box whose
// purchase does, and the box it carries.
export type TreeStep = { tree: number; step: number; via: string | null; box: string | null; x: number; y: number };

// Coloured blocks and spikes: only the active colour collides; a dash swaps them once block swap is bought.
export const TONE_COLORS: Record<"blue" | "orange", string> = { blue: "#42a5f5", orange: "#ffa726" };
export const GROWN_COLOR = "#c5a26b";
export const SPRING_COLOR = "#ffd54f";
export const SPIKE_COLOR = "#e5484d";
export const LEVEL_COLOR = "#8193ab";
// Upgrade boxes (ground-layer triggers: the ground rays land on them).
export const BOX_COLOR = "#bcaaa4";
// Ground and spikes of another world state (off: Area 1 overgrown).
export const OFF_COLOR = "#9ccc65";
export const MARKERS: Record<string, { color: string; text: string; shape: "square" | "small" | "diamond" | "circle" | "star" }> = {
  start: { color: "#3ddc97", text: "Start gate", shape: "square" },
  end: { color: "#ff6b6b", text: "End gate", shape: "square" },
  checkpoint: { color: "#4fc3f7", text: "Checkpoint", shape: "small" },
  teleporter: { color: "#b388ff", text: "Teleporter", shape: "diamond" },
  dashRefill: { color: "#ff80ab", text: "Dash refill", shape: "circle" },
  jumpRefill: { color: "#80cbc4", text: "Jump refill", shape: "circle" },
  fullRefill: { color: "#fff59d", text: "Full refill", shape: "circle" },
  secret: { color: "#ffd740", text: "Secret (a bonus's reward box)", shape: "star" },
};

const polyPath = (p: number[]) => p.reduce((d, v, i) => i % 2 ? `${d} ${fy(v)}` : `${d}${i ? "L" : "M"}${v}`, "") + "Z";

// `springDirs`: each spring's middle, launch direction and how far its box reaches that way, for its arrow.
export type LevelPaths = { ground: string; lines: string; spikes: string; spikeLines: string; boxes: string; grown: string; springs: string;
  springDirs: { cx: number; cy: number; ux: number; uy: number; reach: number }[];
  tone: Record<"blue" | "orange", { ground: string; spikes: string }>; off: { ground: string; spikes: string } };

export function levelPaths(solids: Solid[], boxes: number[][], grown: Solid[]): LevelPaths {
  const out: LevelPaths = { ground: "", lines: "", spikes: "", spikeLines: "", boxes: boxes.map(polyPath).join(""), grown: grown.map(s => polyPath(s.p)).join(""),
    springs: "", springDirs: [], tone: { blue: { ground: "", spikes: "" }, orange: { ground: "", spikes: "" } }, off: { ground: "", spikes: "" } };
  for (const s of solids) {
    if (s.spring && !s.off) {
      const [ux, uy] = s.spring, n = s.p.length / 2;
      let cx = 0, cy = 0, reach = 0;
      for (let i = 0; i < n; i++) { cx += s.p[2 * i] / n; cy += s.p[2 * i + 1] / n; }
      for (let i = 0; i < n; i++) reach = Math.max(reach, (s.p[2 * i] - cx) * ux + (s.p[2 * i + 1] - cy) * uy);
      out.springs += polyPath(s.p);
      out.springDirs.push({ cx, cy, ux, uy, reach });
      continue;
    }
    let d: string;
    if (s.k === "circle") d = `M${s.p[0] - s.r} ${fy(s.p[1])}a${s.r} ${s.r} 0 1 0 ${2 * s.r} 0a${s.r} ${s.r} 0 1 0 ${-2 * s.r} 0Z`;
    else { d = `M${s.p[0]} ${fy(s.p[1])}`; for (let i = 2; i < s.p.length; i += 2) d += `L${s.p[i]} ${fy(s.p[i + 1])}`; }
    const closed = s.k === "poly" || s.k === "circle";
    if (s.k === "poly") d += "Z";
    if (s.off) { if (s.spike) out.off.spikes += d; else out.off.ground += d; }
    else if (s.tone) { if (s.spike) out.tone[s.tone].spikes += d; else out.tone[s.tone].ground += d; }
    else if (s.spike) { if (closed) out.spikes += d; else out.spikeLines += d; }
    else if (closed) out.ground += d;
    else out.lines += d;
  }
  return out;
}

// Which level layers draw; the level's own ground and walls always do.
export type LevelShow = { spikes: boolean; boxes: boolean; blue: boolean; orange: boolean; grown: boolean; springs: boolean; off: boolean };

// Memoized: a pan changes no prop, so the level isn't rebuilt on every pointer move; a zoom (`px`: world units per
// screen pixel) redraws only for the spring arrows. A spring's arrow (from its box's middle out past its far side, the
// way it launches) shows once zoomed in to ARROW_PX or closer; zoomed out a spring is its box alone.
const ARROW_PX = 6;
export const Level = memo(function Level({ paths, show, px }: { paths: LevelPaths; show: LevelShow; px: number }) {
  const arrows = !show.springs || px > ARROW_PX ? "" : paths.springDirs.map(({ cx, cy, ux, uy, reach }) => {
    const len = reach + 40, head = 16, tx = cx + ux * len, ty = cy + uy * len;
    return `M${cx} ${fy(cy)}L${tx} ${fy(ty)}M${tx - head * (ux + uy)} ${fy(ty - head * (uy - ux))}L${tx} ${fy(ty)}L${tx - head * (ux - uy)} ${fy(ty - head * (uy + ux))}`;
  }).join("");
  return <g style={{ pointerEvents: "none" }}>
    <path d={paths.ground} fill={LEVEL_COLOR} fillOpacity={.55}/>
    <path d={paths.lines} fill="none" stroke={LEVEL_COLOR} strokeWidth={1.5} vectorEffect="non-scaling-stroke"/>
    {show.spikes && <>
      <path d={paths.spikes} fill={SPIKE_COLOR} fillOpacity={.85}/>
      <path d={paths.spikeLines} fill="none" stroke={SPIKE_COLOR} strokeWidth={2} vectorEffect="non-scaling-stroke"/>
    </>}
    {(["blue", "orange"] as const).map(t => show[t] && <g key={t}>
      <path d={paths.tone[t].ground} fill={TONE_COLORS[t]} fillOpacity={.45} stroke={TONE_COLORS[t]} strokeWidth={1} vectorEffect="non-scaling-stroke"/>
      <path d={paths.tone[t].spikes} fill="none" stroke={TONE_COLORS[t]} strokeWidth={2.5} strokeDasharray="3 2" vectorEffect="non-scaling-stroke"/>
    </g>)}
    {show.boxes && <path d={paths.boxes} fill={BOX_COLOR} fillOpacity={.12} stroke={BOX_COLOR} strokeWidth={1} vectorEffect="non-scaling-stroke"/>}
    {show.off && <>
      <path d={paths.off.ground} fill="none" stroke={OFF_COLOR} strokeWidth={1.5} strokeDasharray="5 3" vectorEffect="non-scaling-stroke"/>
      <path d={paths.off.spikes} fill="none" stroke={SPIKE_COLOR} strokeWidth={1.5} strokeDasharray="2 3" vectorEffect="non-scaling-stroke"/>
    </>}
    {show.grown && <path d={paths.grown} fill={GROWN_COLOR} fillOpacity={.3} stroke={GROWN_COLOR} strokeWidth={1.5} strokeDasharray="6 4" vectorEffect="non-scaling-stroke"/>}
    {show.springs && <path d={paths.springs} fill={SPRING_COLOR} fillOpacity={.8} stroke={SPRING_COLOR} strokeWidth={1.5} strokeLinejoin="round" vectorEffect="non-scaling-stroke"/>}
    <path d={arrows} fill="none" stroke={SPRING_COLOR} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" vectorEffect="non-scaling-stroke"/>
  </g>;
});

// Markers and tree purchase numbers, a constant size on screen (`px`: world units per screen pixel), over each
// checkpoint's trigger area and secret's box (world size; no pointer events, so floors under it stay tappable). `kinds`:
// the marker kinds drawn. Memoized: a pan keeps px, so only a zoom redraws them.
export const Marks = memo(function Marks({ markers, steps, px, kinds, areas, showSteps }: { markers: Marker[]; steps: TreeStep[]; px: number; kinds: Record<string, boolean>; areas: boolean; showSteps: boolean }) {
  return <g>
    {areas && markers.map((m, i) => m.area && kinds[m.kind] && <path key={`area${i}`} d={m.area.map(polyPath).join("")} fill={MARKERS[m.kind]?.color} fillOpacity={.1}
      stroke={MARKERS[m.kind]?.color} strokeWidth={1.5} strokeDasharray="5 3" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>)}
    {markers.map((m, i) => {
      const look = MARKERS[m.kind]; if (!look || !kinds[m.kind]) return null;
      const s = (look.shape === "small" ? 4 : look.shape === "star" ? 10 : 7) * px, x = m.x, y = fy(m.y);
      const d = look.shape === "diamond" ? `M${x} ${y - s * 1.3}L${x + s} ${y}L${x} ${y + s * 1.3}L${x - s} ${y}Z`
        : look.shape === "circle" ? `M${x - s} ${y}a${s} ${s} 0 1 0 ${2 * s} 0a${s} ${s} 0 1 0 ${-2 * s} 0Z`
        : look.shape === "star" ? starPath(x, y, s)
        : `M${x - s} ${y - s}h${2 * s}v${2 * s}h${-2 * s}Z`;
      return <path key={i} d={d} fill={look.color} fillOpacity={look.shape === "circle" ? .8 : 1} stroke="#07111f" strokeWidth={1} vectorEffect="non-scaling-stroke"><title>{`${look.text}: ${m.name}`}</title></path>;
    })}
    {showSteps && steps.map((t, i) => <text key={i} x={t.x} y={fy(t.y) + 5 * px} fill={GROWN_COLOR} fontSize={14 * px} fontWeight={700} textAnchor="middle"
      stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke">{t.step || "?"}<title>{t.step ? `Tree ${t.tree} · purchase ${t.step} grows this${t.via ? ` (bought at ${t.via})` : ""}${t.box ? `; its box: ${t.box}` : ""}`
        : `Tree ${t.tree} · no box found that grows this`}</title></text>)}
  </g>;
});

// A five-pointed star of outer radius `r` at (x, y), SVG coordinates.
export function starPath(x: number, y: number, r: number) {
  let d = "";
  for (let i = 0; i < 10; i++) {
    const a = -Math.PI / 2 + i * Math.PI / 5, k = i % 2 ? r * .45 : r;
    d += `${i ? "L" : "M"}${x + k * Math.cos(a)} ${y + k * Math.sin(a)}`;
  }
  return d + "Z";
}
