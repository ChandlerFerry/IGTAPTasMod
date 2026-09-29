"use client";

import { useEffect, useState } from "react";
import "./techs.css";

export type TechCategory = "glitchless" | "nmg" | "major";
export type TechOrigin = "community" | "game" | "ours";
export type TechRequires = "NONE" | "WJ" | "WJ_DS" | "WJ_DS_DJ" | "WJ_DS_DJ_BS" | "OMNI";
export type Tech = { id: string; name: string; category: TechCategory; requires: TechRequires; origin: TechOrigin; confirmed: boolean; omniDash?: boolean; floatingOrigin?: boolean; fastForward?: number; description: string; note?: string | null; detector?: string | null;
  related: string[]; start?: string | null; routeId?: string | null; savestate: boolean; ticks: number; labmap?: string | null };
export type LabShape = { kind: string; at?: number[] | null; size?: number[] | null; points?: number[][] | null; layer: string; tag: string; trigger: boolean; spike: boolean };
export type LabInfo = { id: string; tech?: string | null; name?: string; category?: string; techNote?: string | null; anchor?: number[]; region?: number[] | null; finish?: number[];
  start?: { x?: number | null; y?: number | null; vx?: number | null; vy?: number | null; facing?: number | null; fields?: Record<string, unknown> | null };
  shapes?: LabShape[]; hand?: { shapes: LabShape[]; start?: { x?: number | null; y?: number | null; facing?: number | null } | null } | null;
  demoTicks?: number | null; demoInputTicks?: number | null; demoPath?: number[][] | null; error?: string };
export type TechSeen = { detector: string; label: string; start: number; end: number; input: number };
export type TechState = { list: Tech[]; detectors: { id: string; label: string }[]; labs?: LabInfo[]; injectedLab?: string | null;
  seen: { routeId: string; route: string; running: boolean; items: TechSeen[] } | null };

type Command = (path: string, body?: unknown) => Promise<boolean>;

const CATEGORIES: [TechCategory, string, string][] = [
  ["glitchless", "Glitchless", "The game working as made."],
  ["nmg", "No Major Glitches", "Collision and timing quirks: clips, ground rays, grace frames."],
  ["major", "Major Glitches", "Breaks the game's rules outright."],
];
const REQUIRES: [TechRequires, string][] = [
  ["NONE", "No unlocks"], ["WJ", "WJ"], ["WJ_DS", "WJ + DS"], ["WJ_DS_DJ", "WJ + DS + DJ"], ["WJ_DS_DJ_BS", "WJ + DS + DJ + BS"],
  ["OMNI", "Omnidash"],
];
const STATUSES: [boolean, string, string][] = [
  [true, "Confirmed", "Has a reproduction: a demo route that performs it."],
  [false, "Unconfirmed", "No demo yet. Add one to confirm it, or merge or delete it."],
];
const ORIGINS: [TechOrigin, string, string][] = [
  ["community", "Community", "From the community's tech list"],
  ["game", "Game", "Named in the game's own code"],
  ["ours", "Ours", "Named in this repo: check it isn't a community tech renamed."],
];
const TICK_SECONDS = 0.02;

export function TechPanel({techs, running, selectedRoute, onCommand, onEditInputs}: {techs?: TechState; running: boolean; selectedRoute?: {id: string; name: string};
  onCommand: Command; onEditInputs: (routeId: string) => void}) {
  const [query, setQuery] = useState("");
  const [oursOnly, setOursOnly] = useState(false);
  const [editing, setEditing] = useState("");
  const [focus, setFocus] = useState("");
  const [newName, setNewName] = useState("");
  const [newCategory, setNewCategory] = useState<TechCategory>("nmg");
  const [newRequires, setNewRequires] = useState<TechRequires>("NONE");
  useEffect(() => { if (!focus) return; document.getElementById(`tech-${focus}`)?.scrollIntoView({ behavior: "smooth", block: "center" }); const t = window.setTimeout(() => setFocus(""), 1600); return () => window.clearTimeout(t); }, [focus]);
  if (!techs) return <div className="panel"><div className="panelTitle"><div><p className="eyebrow">NAMED MOVEMENT</p><h2>Tech</h2></div></div>
    <p className="lede">The game plugin reports no techs: older version, or not connected.</p></div>;

  const byId = new Map(techs.list.map(t => [t.id, t]));
  const byDetector = new Map(techs.list.filter(t => t.detector).map(t => [t.detector as string, t]));
  const q = query.trim().toLowerCase();
  const shown = techs.list.filter(t => (!oursOnly || t.origin === "ours")
    && (!q || `${t.name} ${t.description} ${t.note ?? ""}`.toLowerCase().includes(q)));
  const seen = techs.seen;
  const seenCounts = new Map<string, number>();
  for (const s of seen?.items ?? []) seenCounts.set(s.detector, (seenCounts.get(s.detector) ?? 0) + 1);

  return <div className="panel techPanel">
    <div className="panelTitle"><div><p className="eyebrow">NAMED MOVEMENT</p><h2>Tech</h2></div>
      <span className="hint">{techs.list.length} techs · {techs.list.filter(t => t.routeId).length} with a demo</span></div>
    <p className="lede">Every named tech with a demo. Playback detects techs and captures their demos.</p>
    <details className="howTo"><summary>How it works</summary><ol>
      <li>Demo capture: savestate at detection &minus;25 t, inputs through +25 t.</li>
      <li>Old demos without savestates gain one the same way.</li>
      <li><b>Use selected route</b> keeps the route and copies its savestate.</li>
      <li>Renaming a tech renames its in-game overlay label too.</li>
      <li><b>OURS</b> marks our names: merge ones the community named differently.</li>
    </ol></details>

    <div className="techToolbar">
      <input className="techSearch" value={query} placeholder="Search techs" onChange={e => setQuery(e.target.value)} />
      <label className={`capabilityLock executable ${oursOnly ? "on" : ""}`}><input type="checkbox" checked={oursOnly} onChange={e => setOursOnly(e.target.checked)} /><strong>Only ours</strong></label>
      <div className="techAdd">
        <input value={newName} placeholder="New tech name" onChange={e => setNewName(e.target.value)} />
        <select value={newRequires} onChange={e => setNewRequires(e.target.value as TechRequires)}>{REQUIRES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
        <select value={newCategory} onChange={e => setNewCategory(e.target.value as TechCategory)}>{CATEGORIES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
        <button className="secondary" disabled={!newName.trim()} title={newName.trim() ? undefined : "Name the tech first"} onClick={async () => { if (await onCommand("/techs/add", { name: newName.trim(), category: newCategory, requires: newRequires })) setNewName(""); }}>+ Add tech</button>
      </div>
    </div>
    <div className="techLegend">{CATEGORIES.map(([id, label, blurb]) => <span key={id} className={`legendItem ${id}`} title={blurb}>{label}</span>)}</div>

    {seen && <div className="techSeen">
      <div><p className="eyebrow">{seen.running ? "PLAYING NOW" : "LAST PLAYBACK"}</p><strong>{seen.route}</strong></div>
      <div className="techSeenList">
        {seenCounts.size === 0 && <span className="muted">No tech detected{seen.running ? " yet" : ""}.</span>}
        {[...seenCounts].map(([detector, count]) => { const tech = byDetector.get(detector); const label = seen.items.find(s => s.detector === detector)?.label ?? detector;
          return <button key={detector} className="seenChip" title={tech ? `Show ${tech.name}` : "No tech has this detector"} disabled={!tech} onClick={() => tech && setFocus(tech.id)}>{label}{count > 1 ? ` ×${count}` : ""}</button>; })}
      </div>
    </div>}

    {!!techs.labs?.length && <LabLoader labs={techs.labs} injected={techs.injectedLab ?? null} byId={byId} running={running} onCommand={onCommand} onFocus={setFocus} />}

    {STATUSES.map(([confirmed, title, blurb]) => {
      const group = shown.filter(t => t.confirmed === confirmed);
      return <div key={title} className={`techGroup ${confirmed ? "confirmed" : "unconfirmed"}`}>
        <div className="techGroupHead"><h3>{title}</h3><span>{group.length}</span><p className="techBlurb">{blurb}</p></div>
        {REQUIRES.map(([requires, label]) => {
          const list = group.filter(t => t.requires === requires);
          if (!list.length) return null;
          return <section key={requires} className="techTier">
            <header><h4>{label}</h4><span>{list.length}</span></header>
            <div className="techGrid">
              {list.map(t => <TechCard key={t.id} tech={t} byId={byId} techs={techs} running={running} selectedRoute={selectedRoute} editing={editing === t.id} focused={focus === t.id}
                onEdit={() => setEditing(editing === t.id ? "" : t.id)} onFocus={setFocus} onCommand={onCommand} onEditInputs={onEditInputs} />)}
            </div>
          </section>;
        })}
        {!group.length && <p className="muted">No {title.toLowerCase()} techs{q || oursOnly ? " match" : ""}.</p>}
      </div>;
    })}
  </div>;
}

function TechCard({tech, byId, techs, running, selectedRoute, editing, focused, onEdit, onFocus, onCommand, onEditInputs}: {tech: Tech; byId: Map<string, Tech>; techs: TechState; running: boolean;
  selectedRoute?: {id: string; name: string}; editing: boolean; focused: boolean; onEdit: () => void; onFocus: (id: string) => void; onCommand: Command; onEditInputs: (routeId: string) => void}) {
  const seen = techs.seen;
  const lab = tech.labmap ? techs.labs?.find(l => l.id === tech.labmap) : undefined;
  const detectorLabel = tech.detector ? techs.detectors.find(d => d.id === tech.detector)?.label ?? tech.detector : null;
  const playedDemo = seen && tech.routeId && seen.routeId === tech.routeId && !seen.running;
  const sawIt = !!tech.detector && !!seen?.items.some(s => s.detector === tech.detector);
  const firstSeen = tech.detector ? seen?.items.findIndex(s => s.detector === tech.detector) ?? -1 : -1;
  const related = tech.related.map(id => byId.get(id)).filter((t): t is Tech => !!t);
  const relatedBy = techs.list.filter(t => t.id !== tech.id && t.related.includes(tech.id) && !tech.related.includes(t.id));
  // First sentence stays visible; the rest of the description folds under More.
  const [lead, rest] = /^([\s\S]+?[.!?])\s+([\s\S]+)$/.exec(tech.description ?? "")?.slice(1) ?? [tech.description ?? "", ""];
  return <article id={`tech-${tech.id}`} className={`techCard ${tech.category} ${tech.origin} ${focused ? "focused" : ""}`}
    title={CATEGORIES.find(c => c[0] === tech.category)?.[1]}>
    <div className="techHead">
      <strong>{tech.name}</strong>
      <span className={`originBadge ${tech.origin}`} title={ORIGINS.find(o => o[0] === tech.origin)?.[2]}>{ORIGINS.find(o => o[0] === tech.origin)?.[1].toUpperCase()}</span>
      {detectorLabel && <span className="detectorChip" title={`Detected in playback; overlay label ${tech.name.toUpperCase()}`}>detected</span>}
    </div>
    {lead && <p className="techDescription">{lead}</p>}
    {(rest || tech.note) && <details className="techMore"><summary>More</summary>
      {rest && <p className="techDescription">{rest}</p>}
      {tech.note && <p className="techNote">{tech.note}</p>}
    </details>}
    {(related.length > 0 || relatedBy.length > 0) && <div className={`techRelated ${tech.origin === "ours" ? "merge" : ""}`}>
      <span>{tech.origin === "ours" ? "Same as?" : "Related"}</span>
      {[...related, ...relatedBy].map(r => <button key={r.id} className={`relatedChip ${r.origin}`} onClick={() => onFocus(r.id)} title={`${r.name} (${r.origin})`}>{r.name}</button>)}
    </div>}
    <div className="techDemo">
      {tech.routeId
        ? <span className="demoInfo" title={tech.savestate ? `Own savestate; ${tech.start} unlocks if that's a category`
            : tech.start ? `Starts at ${tech.start}'s course restart and unlocks, or new game` : "Starts at the quick restart where you are"}>
            Demo · {tech.ticks} t · {(tech.ticks * TICK_SECONDS).toFixed(2)}s{tech.savestate ? " · from its savestate" : tech.start ? ` · from ${tech.start}` : ""}{tech.labmap ? ` · in the injected lab map ${tech.labmap}` : ""}{tech.fastForward ? `, turbo to input ${tech.fastForward}` : ""}{tech.omniDash ? " · + omnidash" : ""}{tech.floatingOrigin ? " · + origin overlay" : ""}</span>
        : <span className="demoInfo missing">No demo yet</span>}
      {playedDemo && tech.detector && <span className={`verdict ${sawIt ? "good" : "bad"}`}>{sawIt ? "✓ performed in the last play" : "✗ not detected in the last play"}</span>}
      <div className="buttonRow">
        <button className="secondary" disabled={!tech.routeId || running} title={running ? "Finish playback or recording first" : tech.routeId ? undefined : "Add a demo first"} onClick={() => onCommand("/techs/play", { id: tech.id })}>▶ Play{tech.labmap ? " lab" : ""}</button>
        <button className="secondary" disabled={!tech.routeId || running} title={running ? "Finish playback or recording first" : tech.routeId ? "Game time slowed 10×" : "Add a demo first"} onClick={() => onCommand("/techs/play", { id: tech.id, walk: true })}>⏵ Walk</button>
        {firstSeen >= 0 && !tech.routeId && <button className="secondary" disabled={running} title={running ? "Finish playback or recording first" : undefined} onClick={() => onCommand("/techs/demo", { id: tech.id, from: "playback", seen: firstSeen })}>Demo from last playback</button>}
        <button className="secondary" onClick={onEdit}>{editing ? "Close" : "Edit"}</button>
      </div>
    </div>
    {lab && <div className="techLab" title={lab.anchor ? `${lab.shapes?.length ?? 0} shapes at anchor ${lab.anchor[0]},${lab.anchor[1]} (world frame)` : undefined}>
      <div className="techLabHead"><strong>Lab map</strong><code>{lab.id}</code>
        {lab.demoTicks ? <span className="demoInfo">solved exactly · {lab.demoTicks} physics ticks · {lab.demoInputTicks} inputs</span>
          : <span className="demoInfo missing">{lab.error ?? "not solved yet"}</span>}
        <LabButtons lab={lab} injected={techs.injectedLab === lab.id} running={running} onCommand={onCommand} />
      </div>
      {!!lab.shapes?.length && <LabPreview lab={lab} />}
      {lab.techNote && <details className="techMore"><summary>Lab notes</summary><p className="techNote">{lab.techNote}</p></details>}
    </div>}
    {editing && <TechEditor tech={tech} techs={techs} running={running} selectedRoute={selectedRoute} onCommand={onCommand} onEditInputs={onEditInputs} onClose={onEdit} />}
  </article>;
}

function LabButtons({lab, injected, running, onCommand}: {lab: LabInfo; injected: boolean; running: boolean; onCommand: Command}) {
  const unlocks = lab.category ? ` with ${lab.category} unlocks` : "";
  return <span className="buttonRow">
    <button className="secondary" disabled={running || !!lab.error} title={running ? "Finish playback or recording first" : lab.error ? "Solve the lab first" : `Builds the lab${unlocks} and places you at its start`}
      onClick={() => onCommand("/labmap/inject", { id: lab.id, place: true })}>{injected ? "↺ Back to start" : "Load lab"}</button>
    {injected && <button className="secondary" disabled={running} title={running ? "Finish playback or recording first" : "Removes the lab; restores you, unlocks and course"}
      onClick={() => onCommand("/labmap/restore")}>Unload</button>}
  </span>;
}

function LabLoader({labs, injected, byId, running, onCommand, onFocus}: {labs: LabInfo[]; injected: string | null; byId: Map<string, Tech>; running: boolean;
  onCommand: Command; onFocus: (id: string) => void}) {
  return <div className="techLabs">
    <div className="techLabsHead"><div><p className="eyebrow">LAB MAPS</p><h3>Play a lab by hand</h3></div>
      <span className={`labStatus ${injected ? "on" : ""}`}>{injected ? `Loaded: ${injected}` : "No lab loaded"}</span></div>
    <p className="techBlurb">Practice labs built away from every route. Nothing saves while loaded.</p>
    <details className="howTo"><summary>How lab maps work</summary><ol>
      <li>Load builds the lab and stands you on its floor with its unlocks.</li>
      <li>Dying, quick restart, or Load again returns you to the lab start.</li>
      <li>Unload restores you, your unlocks and your course.</li>
      <li>Scene changes and playback ends unload the lab too.</li>
      <li>Nothing is autosaved; no route saves while a lab loads.</li>
      <li>The dashed floor appears only in hand play, not demos.</li>
    </ol></details>
    <div className="techLabGrid">
      {labs.map(lab => { const tech = lab.tech ? byId.get(lab.tech) : undefined;
        return <div key={lab.id} className={`techLab ${injected === lab.id ? "loaded" : ""}`}>
          <div className="techLabHead"><strong>{lab.name && lab.name !== lab.id ? lab.name : lab.id}</strong>{lab.category && <code>{lab.category}</code>}
            {tech && <button className="relatedChip" onClick={() => onFocus(tech.id)} title={`Show ${tech.name}`}>{tech.name}</button>}
            <LabButtons lab={lab} injected={injected === lab.id} running={running} onCommand={onCommand} /></div>
          {lab.error ? <p className="demoInfo missing">{lab.error}</p> : !!lab.shapes?.length && <LabPreview lab={lab} />}
        </div>; })}
    </div>
  </div>;
}

function LabPreview({lab}: {lab: LabInfo}) {
  // The lab drawn in its own frame: shapes relative to the anchor, the start's dot and arrow, the finish box dashed; the
  // hand-play floor (only there when you play it) outlined, and where you stand for it in green.
  const parts: {x: number; y: number; w: number; h: number; fill: string; hand: boolean}[] = [];
  const lines: {points: string; stroke: string}[] = [];
  const shapes = [...(lab.shapes ?? []).map(s => ({ s, hand: false })), ...(lab.hand?.shapes ?? []).map(s => ({ s, hand: true }))];
  for (const {s, hand} of shapes) {
    const fill = s.spike || s.layer === "Spikes" ? "#c0392b" : s.trigger ? "#8e44ad" : s.tag === "Moss" ? "#27ae60" : "#7f8c8d";
    if (s.kind === "box" && s.at && s.size) parts.push({x: s.at[0] - s.size[0] / 2, y: s.at[1] - s.size[1] / 2, w: s.size[0], h: s.size[1], fill, hand});
    else if (s.points && s.points.length > 1) lines.push({points: s.points.map(p => `${p[0]},${p[1]}`).join(" "), stroke: fill});
  }
  const handStart = lab.hand?.start;
  const [fx, fy, fw, fh] = lab.finish ?? [0, 0, 0, 0];
  const start = lab.start ?? {};
  const sx = start.x ?? 0, sy = start.y ?? 0;
  const xs: number[] = [sx, fx - fw / 2, fx + fw / 2], ys: number[] = [sy, fy - fh / 2, fy + fh / 2];
  if (handStart) { xs.push(handStart.x ?? 0); ys.push(handStart.y ?? 0); }
  for (const b of parts) { xs.push(b.x, b.x + b.w); ys.push(b.y, b.y + b.h); }
  for (const [x, y] of lab.demoPath ?? []) { xs.push(x); ys.push(y); }
  for (const l of lines) for (const p of l.points.split(" ")) { const [x, y] = p.split(",").map(Number); xs.push(x); ys.push(y); }
  const pad = Math.max(20, (Math.max(...xs) - Math.min(...xs)) * 0.05);
  const x0 = Math.min(...xs) - pad, x1 = Math.max(...xs) + pad, y0 = Math.min(...ys) - pad, y1 = Math.max(...ys) + pad;
  const flip = (y: number) => y0 + y1 - y;
  return <svg viewBox={`${x0} ${flip(y1)} ${x1 - x0} ${y1 - y0}`} style={{width: "100%", maxHeight: 180, background: "#11151c", borderRadius: 6, margin: "4px 0"}}>
    {parts.map((b, i) => <rect key={i} x={b.x} y={flip(b.y + b.h)} width={b.w} height={b.h} fill={b.hand ? "none" : b.fill} stroke={b.hand ? "#5fd4ff" : "none"} strokeWidth={4} strokeDasharray={b.hand ? "10 8" : undefined} opacity={0.85} />)}
    {lines.map((l, i) => <polyline key={i} points={l.points.split(" ").map(p => { const [x, y] = p.split(",").map(Number); return `${x},${flip(y)}`; }).join(" ")} fill="none" stroke={l.stroke} strokeWidth={4} />)}
    {lab.demoPath && lab.demoPath.length > 1 && <polyline points={lab.demoPath.map(([x, y]) => `${x},${flip(y)}`).join(" ")} fill="none" stroke="#9fe3ff" strokeWidth={3} strokeDasharray="6 6" opacity={0.8} />}
    <rect x={fx - fw / 2} y={flip(fy + fh / 2)} width={fw} height={fh} fill="none" stroke="#f1c40f" strokeWidth={6} strokeDasharray="18 12" />
    <circle cx={sx} cy={flip(sy)} r={Math.max(6, (x1 - x0) * 0.012)} fill="#fff" />
    <line x1={sx} y1={flip(sy)} x2={sx + (start.vx ?? 0) * 0.12} y2={flip(sy + (start.vy ?? 0) * 0.12)} stroke="#fff" strokeWidth={5} />
    {handStart && <circle cx={handStart.x ?? 0} cy={flip(handStart.y ?? 0)} r={Math.max(6, (x1 - x0) * 0.012)} fill="#2ecc71" />}
  </svg>;
}

function TechEditor({tech, techs, running, selectedRoute, onCommand, onEditInputs, onClose}: {tech: Tech; techs: TechState; running: boolean; selectedRoute?: {id: string; name: string};
  onCommand: Command; onEditInputs: (routeId: string) => void; onClose: () => void}) {
  const [draft, setDraft] = useState({ name: tech.name, category: tech.category, requires: tech.requires, origin: tech.origin, detector: tech.detector ?? "", start: tech.start ?? "", labmap: tech.labmap ?? "",
    floatingOrigin: !!tech.floatingOrigin, fastForward: tech.fastForward ?? 0, description: tech.description, note: tech.note ?? "", related: tech.related });
  const [into, setInto] = useState("");
  const [cut, setCut] = useState(-1);
  const set = (u: Partial<typeof draft>) => setDraft(d => ({ ...d, ...u }));
  const others = techs.list.filter(t => t.id !== tech.id).sort((a, b) => a.name.localeCompare(b.name));
  const taken = new Map(techs.list.filter(t => t.id !== tech.id && t.detector).map(t => [t.detector as string, t.name]));
  const seen = techs.seen;
  return <div className="techEditor">
    <div className="techEditorGrid">
      <label className="field"><span>Name</span><input value={draft.name} onChange={e => set({ name: e.target.value })} /></label>
      <label className="field"><span>Category</span><select value={draft.category} onChange={e => set({ category: e.target.value as TechCategory })}>{CATEGORIES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      <label className="field"><span>Requires</span><select value={draft.requires} onChange={e => set({ requires: e.target.value as TechRequires })}>{REQUIRES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      <label className="field"><span>Named by</span><select value={draft.origin} onChange={e => set({ origin: e.target.value as TechOrigin })}>{ORIGINS.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
      <label className="field"><span>Detector (in-game overlay)</span><select value={draft.detector} onChange={e => set({ detector: e.target.value })}>
        <option value="">None</option>
        {techs.detectors.map(d => <option key={d.id} value={d.id} disabled={taken.has(d.id)}>{d.label}{taken.has(d.id) ? ` (${taken.get(d.id)})` : ""}</option>)}</select></label>
      <label className="field"><span>Demo start (2_WJ_DS, fulltas/VMAN_NONE, or empty)</span><input value={draft.start} placeholder="empty: savestate or quick restart" onChange={e => set({ start: e.target.value })} /></label>
      <label className="field" title="Demo plays on the injected lab map, then restores. Lab-only: never a route">
        <span>Lab map id (labmaps/&lt;id&gt;.json)</span><input value={draft.labmap} placeholder="empty: no lab map" onChange={e => set({ labmap: e.target.value.trim() })} /></label>
      <label className="field" title="Draws the FloatingOrigin overlay; always replays from its start: savestates lack the origin">
        <span>FloatingOrigin demo</span><input type="checkbox" checked={draft.floatingOrigin} onChange={e => set({ floatingOrigin: e.target.checked })} /></label>
      <label className="field" title="Turbo for this many first input ticks after the new game loads">
        <span>Fast-forward (input ticks in turbo)</span><input type="number" min={0} value={draft.fastForward} onChange={e => set({ fastForward: Math.max(0, Number(e.target.value) || 0) })} /></label>
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

    <p className="eyebrow techEditorHead">DEMO</p>
    <details className="howTo"><summary>How demos start</summary><ul>
      <li>Empty start: the demo uses its savestate, else your quick restart.</li>
      <li>Named start: the course&apos;s quick restart and unlocks, or new game.</li>
      <li>Lab map: the demo plays on the injected lab, then restores.</li>
      <li>FloatingOrigin: savestates never store the world origin, so it replays fully.</li>
      <li>Fast-forward: the first input ticks run in turbo after the new game.</li>
    </ul></details>
    <div className="buttonRow">
      <button className="secondary" disabled={running || !tech.labmap} title={running ? "Finish playback or recording first" : tech.labmap ? "The solved lab demo, played on the injected lab map" : "Add a lab map id first"}
        onClick={() => onCommand("/techs/demo", { id: tech.id, from: "lab", labmap: tech.labmap })}>Use lab demo{tech.labmap ? ` (${tech.labmap})` : ""}</button>
      <button className="secondary" disabled={running || !selectedRoute || selectedRoute.id === tech.routeId} title={running ? "Finish playback or recording first" : selectedRoute ? `The whole route, from its own start: ${selectedRoute.name}` : "Select a route in the sidebar"}
        onClick={() => onCommand("/techs/demo", { id: tech.id, from: "selected" })}>Use selected route{selectedRoute && selectedRoute.id !== tech.routeId ? ` (${selectedRoute.name})` : ""}</button>
      {seen && seen.items.length > 0 && <>
        <select value={cut} onChange={e => setCut(Number(e.target.value))}><option value={-1}>Cut last playback after…</option>
          {seen.items.map((s, i) => <option key={i} value={i}>{s.label} · tick {s.end}</option>)}</select>
        <button className="secondary" disabled={running || cut < 0} title={running ? "Finish playback or recording first" : cut < 0 ? "Pick a cut point first" : "Demo up to the picked tick"} onClick={() => onCommand("/techs/demo", { id: tech.id, from: "playback", seen: cut })}>Cut</button>
      </>}
      <button className="secondary" disabled={!tech.routeId} title={tech.routeId ? undefined : "Add a demo first"} onClick={() => tech.routeId && onEditInputs(tech.routeId)}>Edit inputs</button>
      <button className="danger" disabled={running || !tech.routeId} title={running ? "Finish playback or recording first" : tech.routeId ? "Deletes the demo route" : "Add a demo first"} onClick={() => window.confirm(`Delete ${tech.name}'s demo route?`) && onCommand("/techs/demo", { id: tech.id, from: "none" })}>Remove demo</button>
    </div>

    <p className="eyebrow techEditorHead">MERGE OR DELETE</p>
    <div className="buttonRow">
      <select value={into} onChange={e => setInto(e.target.value)}><option value="">Merge into…</option>{others.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
      <button className="secondary" disabled={!into || running} title={running ? "Finish playback or recording first" : into ? "Merges text, and fills missing detector and demo; then deletes this tech." : "Pick a tech to merge into"}
        onClick={() => window.confirm(`Merge ${tech.name} into ${techs.list.find(t => t.id === into)?.name}?`) && onCommand("/techs/merge", { id: tech.id, into })}>Merge</button>
      <button className="danger" disabled={running} title={running ? "Finish playback or recording first" : "Deletes the tech and its demo"} onClick={() => window.confirm(`Delete ${tech.name}${tech.routeId ? " and its demo" : ""}?`) && onCommand("/techs/delete", { id: tech.id })}>Delete tech</button>
    </div>
  </div>;
}
