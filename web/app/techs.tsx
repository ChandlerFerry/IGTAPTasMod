"use client";

import { useEffect, useState } from "react";
import "./techs.css";
import { SPRING_COLOR } from "./ultra-level";

export type TechCategory = "glitchless" | "nmg" | "major";
export type TechRequires = "NONE" | "WJ" | "WJ_DS" | "WJ_DS_DJ" | "WJ_DS_DJ_BS" | "DS_OM";
export type TechLabRef = { id: string; demo?: string | null; ticks?: number | null; inputTicks?: number | null };
export type Tech = { id: string; name: string; category: TechCategory; requires: TechRequires; floatingOrigin?: boolean;
  description: string; note?: string | null; detector?: string | null; related: string[]; labmap?: string | null; labDemo?: string | null; graft?: string | null;
  labs: TechLabRef[] };
export type LabShape = { kind: string; at?: number[] | null; size?: number[] | null; points?: number[][] | null; layer: string; tag: string; trigger: boolean; spike: boolean;
  rotation?: number; spring?: { upForce: number; strength: number; movementLock: number } | null };
export type LabInfo = { id: string; tech?: string | null; name?: string; category?: string; techNote?: string | null; anchor?: number[]; region?: number[] | null; finish?: number[];
  start?: { x?: number | null; y?: number | null; vx?: number | null; vy?: number | null; facing?: number | null; fields?: Record<string, unknown> | null };
  shapes?: LabShape[]; hand?: { shapes: LabShape[]; start?: { x?: number | null; y?: number | null; facing?: number | null } | null } | null;
  ghosts?: { label?: string | null; demo?: string | null; path?: number[][] | null }[] | null; error?: string };
type TechSeen = { detector: string; label: string; start: number; end: number; input: number };
export type TechState = { list: Tech[]; detectors: { id: string; label: string }[]; labs?: LabInfo[]; injectedLab?: string | null;
  seen: { routeId: string; route: string; running: boolean; lab?: { tech: string; lab: string } | null; items: TechSeen[] } | null };

type Command = (path: string, body?: unknown) => Promise<boolean>;

const CATEGORIES: [TechCategory, string, string][] = [
  ["glitchless", "Glitchless", "The game working as made."],
  ["nmg", "No Major Glitches", "Collision and timing quirks: clips, ground rays, grace frames."],
  ["major", "Major Glitches", "Breaks the game's rules outright."],
];
const REQUIRES: [TechRequires, string][] = [
  ["NONE", "No unlocks"], ["WJ", "WJ"], ["WJ_DS", "WJ + DS"], ["WJ_DS_DJ", "WJ + DS + DJ"], ["WJ_DS_DJ_BS", "WJ + DS + DJ + BS"],
  ["DS_OM", "Omnidash (DS + OM)"],
];
const TICK_SECONDS = 0.02;
const BUSY = "Finish playback or recording first";

export function TechPanel({techs, running, onCommand}: {techs?: TechState; running: boolean; onCommand: Command}) {
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState("");
  const [focus, setFocus] = useState("");
  useEffect(() => { if (!focus) return; document.getElementById(`tech-${focus}`)?.scrollIntoView({ behavior: "smooth", block: "center" }); const t = window.setTimeout(() => setFocus(""), 1600); return () => window.clearTimeout(t); }, [focus]);
  if (!techs) return <div className="panel"><div className="panelTitle"><div><p className="eyebrow">NAMED MOVEMENT</p><h2>Tech</h2></div></div>
    <p className="lede">The game plugin reports no techs: older version, or not connected.</p></div>;

  const byId = new Map(techs.list.map(t => [t.id, t]));
  const reproduced = techs.list.filter(t => t.labs.length > 0);
  const missing = techs.list.filter(t => t.labs.length === 0);
  const byDetector = new Map(reproduced.filter(t => t.detector).map(t => [t.detector as string, t]));
  const q = query.trim().toLowerCase();
  const matches = (t: Tech) => (!q || `${t.name} ${t.description} ${t.note ?? ""}`.toLowerCase().includes(q));
  const shown = reproduced.filter(matches);
  const tiers: [string, string][] = [...REQUIRES, ...[...new Set(shown.map(t => t.requires as string))].filter(r => !REQUIRES.some(([id]) => id === r)).map(r => [r, r.split("_").join(" + ")] as [string, string])];
  const labCount = new Set(reproduced.flatMap(t => t.labs.map(l => l.id))).size;
  const injected = techs.injectedLab ?? null;
  const seen = techs.seen;
  const seenCounts = new Map<string, number>();
  for (const s of seen?.items ?? []) seenCounts.set(s.detector, (seenCounts.get(s.detector) ?? 0) + 1);

  return <div className="panel techPanel">
    <div className="panelTitle"><div><p className="eyebrow">NAMED MOVEMENT</p><h2>Tech</h2></div>
      <span className="hint">{reproduced.length} techs · {labCount} labs</span>
      <span className={`labStatus ${injected ? "on" : ""}`}>{injected ? `Lab loaded: ${injected}` : "No lab loaded"}</span></div>
    <p className="lede">Every tech is a lab demo: a piece of level built away from every route, solved exactly. Play or walk its demo, or load the lab and play it by hand.</p>
    <details className="howTo"><summary>How it works</summary><ol>
      <li><b>▶ Play</b> plays the lab&apos;s demo at 1x; <b>⏵ Walk</b> plays it with game time slowed 10×.</li>
      <li>A demo ends with you in its lab at the start; <b>Unload</b> restores you, your unlocks and your course.</li>
      <li><b>Load lab</b> builds the lab and stands you on its floor with its unlocks; dying, quick restart or Back to start return you there.</li>
      <li>The dashed floor appears only in hand play, not in demos. A scene change unloads the lab.</li>
      <li>Nothing is autosaved, and no route saves, while a lab is loaded.</li>
      <li>Renaming a tech renames its in-game overlay label too. <b>OURS</b> marks our names: merge ones the community named differently.</li>
    </ol></details>

    <div className="techToolbar">
      <input className="techSearch" value={query} placeholder="Search techs" onChange={e => setQuery(e.target.value)} />
    </div>
    <div className="techLegend">{CATEGORIES.map(([id, label, blurb]) => <span key={id} className={`legendItem ${id}`} title={blurb}>{label}</span>)}</div>

    {seen && <div className="techSeen">
      <div><p className="eyebrow">{seen.running ? "PLAYING NOW" : "LAST PLAYBACK"}</p><strong>{seen.route}</strong></div>
      <div className="techSeenList">
        {seenCounts.size === 0 && <span className="muted">No tech detected{seen.running ? " yet" : ""}.</span>}
        {[...seenCounts].map(([detector, count]) => { const tech = byDetector.get(detector); const label = seen.items.find(s => s.detector === detector)?.label ?? detector;
          return <button key={detector} className="seenChip" title={tech ? `Show ${tech.name}` : "No tech with a lab has this detector"} disabled={!tech} onClick={() => tech && setFocus(tech.id)}>{label}{count > 1 ? ` ×${count}` : ""}</button>; })}
      </div>
    </div>}

    {tiers.map(([requires, label]) => {
      const list = shown.filter(t => t.requires === requires);
      if (!list.length) return null;
      return <section key={requires} className="techTier">
        <header><h4>{label}</h4><span>{list.length}</span></header>
        <div className="techGrid">
          {list.map(t => <TechCard key={t.id} tech={t} byId={byId} techs={techs} running={running} editing={editing === t.id} focused={focus === t.id}
            onEdit={() => setEditing(editing === t.id ? "" : t.id)} onFocus={setFocus} onCommand={onCommand} />)}
        </div>
      </section>;
    })}
    {!shown.length && <p className="muted">No techs{q ? " match" : ""}.</p>}

    {missing.length > 0 && <details className="techMissing">
      <summary>Not reproduced yet: {missing.length} {missing.length === 1 ? "tech" : "techs"}</summary>
      <p>{missing.map((t, i) => <span key={t.id} title={t.description || undefined}>{i > 0 ? ", " : ""}{t.name}{t.graft === "todo" ? "" : " (no lab)"}</span>)}</p>
    </details>}
  </div>;
}

function TechCard({tech, byId, techs, running, editing, focused, onEdit, onFocus, onCommand}: {tech: Tech; byId: Map<string, Tech>; techs: TechState; running: boolean;
  editing: boolean; focused: boolean; onEdit: () => void; onFocus: (id: string) => void; onCommand: Command}) {
  const detectorLabel = tech.detector ? techs.detectors.find(d => d.id === tech.detector)?.label ?? tech.detector : null;
  const related = tech.related.map(id => byId.get(id)).filter((t): t is Tech => !!t);
  const relatedBy = techs.list.filter(t => t.id !== tech.id && t.related.includes(tech.id) && !tech.related.includes(t.id));
  const [lead, rest] = /^([\s\S]+?[.!?])\s+([\s\S]+)$/.exec(tech.description ?? "")?.slice(1) ?? [tech.description ?? "", ""];
  return <article id={`tech-${tech.id}`} className={`techCard ${tech.category} ${focused ? "focused" : ""}`}
    title={CATEGORIES.find(c => c[0] === tech.category)?.[1]}>
    <div className="techHead">
      <strong>{tech.name}</strong>
      {detectorLabel && <span className="detectorChip" title={`Detected in playback; overlay label ${tech.name.toUpperCase()}`}>detected</span>}
      <button className="secondary techEditButton" onClick={onEdit}>{editing ? "Close" : "Edit"}</button>
    </div>
    {lead && <p className="techDescription">{lead}</p>}
    {(rest || tech.note) && <details className="techMore"><summary>More</summary>
      {rest && <p className="techDescription">{rest}</p>}
      {tech.note && <p className="techNote">{tech.note}</p>}
    </details>}
    {(related.length > 0 || relatedBy.length > 0) && <div className="techRelated">
      <span>Related</span>
      {[...related, ...relatedBy].map(r => <button key={r.id} className="relatedChip" onClick={() => onFocus(r.id)} title={r.name}>{r.name}</button>)}
    </div>}
    {tech.labs.map(ref => <TechLab key={ref.id} tech={tech} lab={ref} info={techs.labs?.find(l => l.id === ref.id)} techs={techs} running={running} onCommand={onCommand} />)}
    {editing && <TechEditor tech={tech} techs={techs} running={running} onCommand={onCommand} onClose={onEdit} />}
  </article>;
}

function TechLab({tech, lab, info, techs, running, onCommand}: {tech: Tech; lab: TechLabRef; info?: LabInfo; techs: TechState; running: boolean; onCommand: Command}) {
  const injected = techs.injectedLab === lab.id;
  const seen = techs.seen;
  const demoFile = `${lab.id}${lab.demo ? `.${lab.demo}` : ""}.demo.json`;
  const error = info?.error ?? (info ? null : "Lab map missing");
  const playable = !error && lab.inputTicks != null;
  const why = running ? BUSY : error ? error : playable ? null : `No demo yet (labmaps/${demoFile})`;
  const played = !!seen && !seen.running && seen.lab?.tech === tech.id && seen.lab?.lab === lab.id;
  const sawIt = !!tech.detector && !!seen?.items.some(s => s.detector === tech.detector);
  const unlocks = info?.category ? ` with ${info.category} unlocks` : "";
  const play = (walk: boolean) => onCommand("/techs/play", { id: tech.id, lab: lab.id, demo: lab.demo ?? "", walk });
  return <div className={`techLab ${injected ? "loaded" : ""}`} title={info?.anchor ? `${info.shapes?.length ?? 0} shapes at anchor ${info.anchor[0]},${info.anchor[1]} (world frame)` : undefined}>
    <div className="techLabHead">
      <strong>{info?.name && info.name !== lab.id ? info.name : "Lab"}</strong><code>{lab.id}{lab.demo ? ` · ${lab.demo}` : ""}</code>
      {info?.category && <code title="The lab's unlocks">{info.category}</code>}
    </div>
    <div className="techLabHead">
      {playable ? <span className="demoInfo" title={`labmaps/${demoFile}`}>Demo · {lab.inputTicks} t · {((lab.inputTicks ?? 0) * TICK_SECONDS).toFixed(2)}s{lab.ticks != null ? ` · finish at ${lab.ticks} t` : ""}</span>
        : <span className="demoInfo missing">{error ?? "No demo yet"}</span>}
      {played && tech.detector && <span className={`verdict ${sawIt ? "good" : "bad"}`}>{sawIt ? "✓ performed in the last play" : "✗ not detected in the last play"}</span>}
      <span className="buttonRow">
        <button className="secondary" disabled={!!why} title={why ?? "Plays the demo at 1x in this lab"} onClick={() => play(false)}>▶ Play</button>
        <button className="secondary" disabled={!!why} title={why ?? "Game time slowed 10×"} onClick={() => play(true)}>⏵ Walk</button>
        <button className="secondary" disabled={running || !!error} title={running ? BUSY : error ?? `Builds the lab${unlocks} and places you at its start`}
          onClick={() => onCommand("/labmap/inject", { id: lab.id, place: true })}>{injected ? "↺ Back to start" : "Load lab"}</button>
        {injected && <button className="secondary" disabled={running} title={running ? BUSY : "Removes the lab; restores you, unlocks and course"}
          onClick={() => onCommand("/labmap/restore")}>Unload</button>}
      </span>
    </div>
    {!!info?.shapes?.length && <LabPreview lab={info} />}
    {info?.techNote && <details className="techMore"><summary>Lab notes</summary><p className="techNote">{info.techNote}</p></details>}
  </div>;
}

const GHOST_COLORS = ["#59d9ff", "#4cf266", "#ff59d9", "#fff233", "#ff9926"];

function LabPreview({lab}: {lab: LabInfo}) {
  const parts: {x: number; y: number; w: number; h: number; fill: string; hand: boolean}[] = [];
  const lines: {points: string; stroke: string}[] = [];
  const polys: {points: number[][]; fill: string}[] = [];
  const shapes = [...(lab.shapes ?? []).map(s => ({ s, hand: false })), ...(lab.hand?.shapes ?? []).map(s => ({ s, hand: true }))];
  for (const {s, hand} of shapes) {
    const fill = s.spike || s.layer === "Spikes" ? "#c0392b" : s.spring ? SPRING_COLOR : s.trigger ? "#8e44ad" : s.tag === "Moss" ? "#27ae60" : "#7f8c8d";
    if (s.kind === "box" && s.at && s.size) {
      const [cx, cy] = s.at, [w, h] = s.size, a = (s.rotation ?? 0) * Math.PI / 180, cos = Math.cos(a), sin = Math.sin(a);
      const turn = (x: number, y: number) => [cx + x * cos - y * sin, cy + x * sin + y * cos];
      if (a) polys.push({points: [turn(w / 2, -h / 2), turn(w / 2, h / 2), turn(-w / 2, h / 2), turn(-w / 2, -h / 2)], fill});
      else parts.push({x: cx - w / 2, y: cy - h / 2, w, h, fill, hand});
      if (s.spring) lines.push({points: [turn(0, h / 2), turn(0, h / 2 + 60), turn(-20, h / 2 + 40), turn(0, h / 2 + 60), turn(20, h / 2 + 40)].map(p => `${p[0]},${p[1]}`).join(" "), stroke: SPRING_COLOR});
    }
    else if (s.points && s.points.length > 1) lines.push({points: s.points.map(p => `${p[0]},${p[1]}`).join(" "), stroke: fill});
  }
  const handStart = lab.hand?.start;
  const [fx, fy, fw, fh] = lab.finish ?? [0, 0, 0, 0];
  const start = lab.start ?? {};
  const sx = start.x ?? 0, sy = start.y ?? 0;
  const xs: number[] = [sx, fx - fw / 2, fx + fw / 2], ys: number[] = [sy, fy - fh / 2, fy + fh / 2];
  if (handStart) { xs.push(handStart.x ?? 0); ys.push(handStart.y ?? 0); }
  for (const b of parts) { xs.push(b.x, b.x + b.w); ys.push(b.y, b.y + b.h); }
  for (const p of polys) for (const [x, y] of p.points) { xs.push(x); ys.push(y); }
  const ghosts = (lab.ghosts ?? []).map((g, i) => ({ ...g, color: GHOST_COLORS[i % GHOST_COLORS.length] })).filter(g => g.path && g.path.length > 1);
  for (const g of ghosts) for (const [x, y] of g.path ?? []) { xs.push(x); ys.push(y); }
  for (const l of lines) for (const p of l.points.split(" ")) { const [x, y] = p.split(",").map(Number); xs.push(x); ys.push(y); }
  const pad = Math.max(20, (Math.max(...xs) - Math.min(...xs)) * 0.05);
  const x0 = Math.min(...xs) - pad, x1 = Math.max(...xs) + pad, y0 = Math.min(...ys) - pad, y1 = Math.max(...ys) + pad;
  const flip = (y: number) => y0 + y1 - y;
  return <>
    <svg viewBox={`${x0} ${flip(y1)} ${x1 - x0} ${y1 - y0}`} style={{width: "100%", maxHeight: 180, background: "#11151c", borderRadius: 6, margin: "4px 0"}}>
      {parts.map((b, i) => <rect key={i} x={b.x} y={flip(b.y + b.h)} width={b.w} height={b.h} fill={b.hand ? "none" : b.fill} stroke={b.hand ? "#5fd4ff" : "none"} strokeWidth={4} strokeDasharray={b.hand ? "10 8" : undefined} opacity={0.85} />)}
      {polys.map((p, i) => <polygon key={i} points={p.points.map(([x, y]) => `${x},${flip(y)}`).join(" ")} fill={p.fill} opacity={0.85} />)}
      {lines.map((l, i) => <polyline key={i} points={l.points.split(" ").map(p => { const [x, y] = p.split(",").map(Number); return `${x},${flip(y)}`; }).join(" ")} fill="none" stroke={l.stroke} strokeWidth={4} />)}
      {ghosts.map((g, i) => <polyline key={i} points={(g.path ?? []).map(([x, y]) => `${x},${flip(y)}`).join(" ")} fill="none" stroke={g.color} strokeWidth={2} strokeDasharray="5 4" vectorEffect="non-scaling-stroke" opacity={0.9} />)}
      <rect x={fx - fw / 2} y={flip(fy + fh / 2)} width={fw} height={fh} fill="none" stroke="#f1c40f" strokeWidth={6} strokeDasharray="18 12" />
      <circle cx={sx} cy={flip(sy)} r={Math.max(6, (x1 - x0) * 0.012)} fill="#fff" />
      <line x1={sx} y1={flip(sy)} x2={sx + (start.vx ?? 0) * 0.12} y2={flip(sy + (start.vy ?? 0) * 0.12)} stroke="#fff" strokeWidth={5} />
      {handStart && <circle cx={handStart.x ?? 0} cy={flip(handStart.y ?? 0)} r={Math.max(6, (x1 - x0) * 0.012)} fill="#2ecc71" />}
    </svg>
    {ghosts.some(g => g.label) && <div className="labGhostLegend">{ghosts.map((g, i) => g.label && <span key={i}><i style={{background: g.color}} />{g.label}</span>)}</div>}
  </>;
}

function TechEditor({tech, techs, running, onCommand, onClose}: {tech: Tech; techs: TechState; running: boolean; onCommand: Command; onClose: () => void}) {
  const [draft, setDraft] = useState({ name: tech.name, category: tech.category, requires: tech.requires, detector: tech.detector ?? "",
    labmap: tech.labmap ?? "", labDemo: tech.labDemo ?? "", floatingOrigin: !!tech.floatingOrigin, description: tech.description, note: tech.note ?? "", related: tech.related });
  const [into, setInto] = useState("");
  const set = (u: Partial<typeof draft>) => setDraft(d => ({ ...d, ...u }));
  const others = techs.list.filter(t => t.id !== tech.id).sort((a, b) => a.name.localeCompare(b.name));
  const taken = new Map(techs.list.filter(t => t.id !== tech.id && t.detector).map(t => [t.detector as string, t.name]));
  return <div className="techEditor">
    <div className="techEditorGrid">
      <label className="field"><span>Name</span><input value={draft.name} onChange={e => set({ name: e.target.value })} /></label>
      <label className="field"><span>Category</span><select value={draft.category} onChange={e => set({ category: e.target.value as TechCategory })}>{CATEGORIES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      <label className="field"><span>Requires</span><select value={draft.requires} onChange={e => set({ requires: e.target.value as TechRequires })}>{REQUIRES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      <label className="field"><span>Detector (in-game overlay)</span><select value={draft.detector} onChange={e => set({ detector: e.target.value })}>
        <option value="">None</option>
        {techs.detectors.map(d => <option key={d.id} value={d.id} disabled={taken.has(d.id)}>{d.label}{taken.has(d.id) ? ` (${taken.get(d.id)})` : ""}</option>)}</select></label>
      <label className="field" title="Draws the FloatingOrigin overlay while its demo plays">
        <span>FloatingOrigin demo</span><input type="checkbox" checked={draft.floatingOrigin} onChange={e => set({ floatingOrigin: e.target.checked })} /></label>
      <label className="field" title={`The tech's own lab, listed first. A lab file whose "tech" is this tech is one of its labs either way`}>
        <span>Own lab (labmaps/&lt;id&gt;.json)</span><input value={draft.labmap} placeholder="empty: only the labs naming it" onChange={e => set({ labmap: e.target.value.trim(), labDemo: e.target.value.trim() ? draft.labDemo : "" })} /></label>
      <label className="field" title="Which of the own lab's demos it plays: labmaps/<lab>.<demo>.demo.json">
        <span>Its demo there</span><input value={draft.labDemo} disabled={!draft.labmap} placeholder="empty: the main demo" onChange={e => set({ labDemo: e.target.value.trim() })} /></label>
    </div>
    <label className="field"><span>Description</span><textarea value={draft.description} onChange={e => set({ description: e.target.value })} /></label>
    <label className="field"><span>Note</span><textarea value={draft.note} onChange={e => set({ note: e.target.value })} /></label>
    <div className="field"><span>Same as / related</span><div className="techRelated">
      {draft.related.map(id => <span key={id} className="relatedChip">{techs.list.find(t => t.id === id)?.name ?? id}<button title="Remove" onClick={() => set({ related: draft.related.filter(r => r !== id) })}>×</button></span>)}
      <select value="" onChange={e => e.target.value && set({ related: [...draft.related, e.target.value] })}><option value="">+ Add…</option>
        {others.filter(t => !draft.related.includes(t.id)).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
    </div></div>
    <div className="buttonRow">
      <button className="primary" disabled={!draft.name.trim()} title={draft.name.trim() ? undefined : "Name the tech first"} onClick={async () => { if (await onCommand("/techs/update", { id: tech.id, ...draft })) onClose(); }}>Save</button>
    </div>

    <p className="eyebrow techEditorHead">MERGE OR DELETE</p>
    <div className="buttonRow">
      <select value={into} onChange={e => setInto(e.target.value)}><option value="">Merge into…</option>{others.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
      <button className="secondary" disabled={!into || running} title={running ? BUSY : into ? "Merges text, and fills a missing detector and lab; then deletes this tech." : "Pick a tech to merge into"}
        onClick={() => window.confirm(`Merge ${tech.name} into ${techs.list.find(t => t.id === into)?.name}?`) && onCommand("/techs/merge", { id: tech.id, into })}>Merge</button>
      <button className="danger" disabled={running} title={running ? BUSY : "Deletes the tech (its lab files stay)"} onClick={() => window.confirm(`Delete ${tech.name}?`) && onCommand("/techs/delete", { id: tech.id })}>Delete tech</button>
    </div>
  </div>;
}
