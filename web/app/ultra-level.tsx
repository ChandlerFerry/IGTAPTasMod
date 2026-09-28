// The level under the Ultra map: solids (springs among them), both colours of coloured blocks, the trees' ground
// with its purchase order, and markers (gates, checkpoints, teleporters, refill orbs). The same file is the public
// viewer's src/level.tsx (github.com/ChandlerFerry/igtap-ultra-map); keep the two in step.

import { memo } from "react";

// `spring`: a spring's trigger box, launching along this unit vector (its transform.up).
export type Solid = { k: "poly" | "line" | "circle" | "capsule"; spike: boolean; p: number[]; r: number; tone?: "blue" | "orange" | null; spring?: number[] | null };
// `area`: a checkpoint's trigger (polygons, x, y pairs): touching it sets where a death or quick restart respawns.
export type Marker = { kind: string; name: string; x: number; y: number; area?: number[][] };
// One tree segment: the fewest tree purchases that grow it (Plant Tree is 1; 0: no box grows it), the box whose
// purchase does, and the box it carries.
export type TreeStep = { tree: number; step: number; via: string | null; box: string | null; x: number; y: number };

// Coloured blocks and spikes: only the active colour collides; a dash swaps them once block swap is bought.
export const TONE_COLORS: Record<"blue" | "orange", string> = { blue: "#42a5f5", orange: "#ffa726" };
export const GROWN_COLOR = "#c5a26b";
export const SPRING_COLOR = "#ffd54f";
const SPIKE_COLOR = "#e5484d";
const LEVEL_COLOR = "#8193ab";
export const MARKERS: Record<string, { color: string; text: string; shape: "square" | "small" | "diamond" | "circle" }> = {
  start: { color: "#3ddc97", text: "Start gate", shape: "square" },
  end: { color: "#ff6b6b", text: "End gate", shape: "square" },
  checkpoint: { color: "#4fc3f7", text: "Checkpoint", shape: "small" },
  teleporter: { color: "#b388ff", text: "Teleporter", shape: "diamond" },
  dashRefill: { color: "#ff80ab", text: "Dash refill", shape: "circle" },
  jumpRefill: { color: "#80cbc4", text: "Jump refill", shape: "circle" },
  fullRefill: { color: "#fff59d", text: "Full refill", shape: "circle" },
};

// Map y up (world) to SVG y down.
const fy = (y: number) => -y;
const polyPath = (p: number[]) => p.reduce((d, v, i) => i % 2 ? `${d} ${fy(v)}` : `${d}${i ? "L" : "M"}${v}`, "") + "Z";

// `springDirs`: each spring's middle, launch direction and how far its box reaches that way, for its arrow.
export type LevelPaths = { ground: string; lines: string; spikes: string; spikeLines: string; boxes: string; grown: string; springs: string;
  springDirs: { cx: number; cy: number; ux: number; uy: number; reach: number }[];
  tone: Record<"blue" | "orange", { ground: string; spikes: string }> };

export function levelPaths(solids: Solid[], boxes: number[][], grown: Solid[]): LevelPaths {
  const out: LevelPaths = { ground: "", lines: "", spikes: "", spikeLines: "", boxes: boxes.map(polyPath).join(""), grown: grown.map(s => polyPath(s.p)).join(""),
    springs: "", springDirs: [], tone: { blue: { ground: "", spikes: "" }, orange: { ground: "", spikes: "" } } };
  for (const s of solids) {
    if (s.spring) {
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
    if (s.tone) { if (s.spike) out.tone[s.tone].spikes += d; else out.tone[s.tone].ground += d; }
    else if (s.spike) { if (closed) out.spikes += d; else out.spikeLines += d; }
    else if (closed) out.ground += d;
    else out.lines += d;
  }
  return out;
}

// Memoized: a pan changes no prop, so the level isn't rebuilt on every pointer move; a zoom (`px`: world units per
// screen pixel) redraws only for the spring arrows. A spring's arrow (from its box's middle out past its far side, the
// way it launches) shows once zoomed in to ARROW_PX or closer; zoomed out a spring is its box alone.
const ARROW_PX = 6;
export const Level = memo(function Level({ paths, grown, px }: { paths: LevelPaths; grown: boolean; px: number }) {
  const arrows = px > ARROW_PX ? "" : paths.springDirs.map(({ cx, cy, ux, uy, reach }) => {
    const len = reach + 40, head = 16, tx = cx + ux * len, ty = cy + uy * len;
    return `M${cx} ${fy(cy)}L${tx} ${fy(ty)}M${tx - head * (ux + uy)} ${fy(ty - head * (uy - ux))}L${tx} ${fy(ty)}L${tx - head * (ux - uy)} ${fy(ty - head * (uy + ux))}`;
  }).join("");
  return <g style={{ pointerEvents: "none" }}>
    <path d={paths.ground} fill={LEVEL_COLOR} fillOpacity={.55}/>
    <path d={paths.lines} fill="none" stroke={LEVEL_COLOR} strokeWidth={1.5} vectorEffect="non-scaling-stroke"/>
    <path d={paths.spikes} fill={SPIKE_COLOR} fillOpacity={.85}/>
    <path d={paths.spikeLines} fill="none" stroke={SPIKE_COLOR} strokeWidth={2} vectorEffect="non-scaling-stroke"/>
    {(["blue", "orange"] as const).map(t => <g key={t}>
      <path d={paths.tone[t].ground} fill={TONE_COLORS[t]} fillOpacity={.45} stroke={TONE_COLORS[t]} strokeWidth={1} vectorEffect="non-scaling-stroke"/>
      <path d={paths.tone[t].spikes} fill="none" stroke={TONE_COLORS[t]} strokeWidth={2.5} strokeDasharray="3 2" vectorEffect="non-scaling-stroke"/>
    </g>)}
    <path d={paths.boxes} fill="#bcaaa4" fillOpacity={.12} stroke="#bcaaa4" strokeWidth={1} vectorEffect="non-scaling-stroke"/>
    {grown && <path d={paths.grown} fill={GROWN_COLOR} fillOpacity={.3} stroke={GROWN_COLOR} strokeWidth={1.5} strokeDasharray="6 4" vectorEffect="non-scaling-stroke"/>}
    <path d={paths.springs} fill={SPRING_COLOR} fillOpacity={.8} stroke={SPRING_COLOR} strokeWidth={1.5} strokeLinejoin="round" vectorEffect="non-scaling-stroke"/>
    <path d={arrows} fill="none" stroke={SPRING_COLOR} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" vectorEffect="non-scaling-stroke"/>
  </g>;
});

// Markers and tree purchase numbers, a constant size on screen (`px`: world units per screen pixel), over each
// checkpoint's trigger area (world size; no pointer events, so floors under it stay tappable). Memoized: a pan
// keeps px, so only a zoom redraws them.
export const Marks = memo(function Marks({ markers, steps, px, showMarkers, showSteps }: { markers: Marker[]; steps: TreeStep[]; px: number; showMarkers: boolean; showSteps: boolean }) {
  return <g>
    {showMarkers && markers.map((m, i) => m.area && <path key={`area${i}`} d={m.area.map(polyPath).join("")} fill={MARKERS[m.kind]?.color} fillOpacity={.1}
      stroke={MARKERS[m.kind]?.color} strokeWidth={1.5} strokeDasharray="5 3" vectorEffect="non-scaling-stroke" style={{ pointerEvents: "none" }}/>)}
    {showMarkers && markers.map((m, i) => {
      const look = MARKERS[m.kind]; if (!look) return null;
      const s = (look.shape === "small" ? 4 : 7) * px, x = m.x, y = fy(m.y);
      const d = look.shape === "diamond" ? `M${x} ${y - s * 1.3}L${x + s} ${y}L${x} ${y + s * 1.3}L${x - s} ${y}Z`
        : look.shape === "circle" ? `M${x - s} ${y}a${s} ${s} 0 1 0 ${2 * s} 0a${s} ${s} 0 1 0 ${-2 * s} 0Z`
        : `M${x - s} ${y - s}h${2 * s}v${2 * s}h${-2 * s}Z`;
      return <path key={i} d={d} fill={look.color} fillOpacity={look.shape === "circle" ? .8 : 1} stroke="#07111f" strokeWidth={1} vectorEffect="non-scaling-stroke"><title>{`${look.text}: ${m.name}`}</title></path>;
    })}
    {showSteps && steps.map((t, i) => <text key={i} x={t.x} y={fy(t.y) + 5 * px} fill={GROWN_COLOR} fontSize={14 * px} fontWeight={700} textAnchor="middle"
      stroke="#07111f" strokeWidth={3 * px} paintOrder="stroke">{t.step || "?"}<title>{t.step ? `Tree ${t.tree} · purchase ${t.step} grows this${t.via ? ` (bought at ${t.via})` : ""}${t.box ? `; its box: ${t.box}` : ""}`
        : `Tree ${t.tree} · no box found that grows this`}</title></text>)}
  </g>;
});
