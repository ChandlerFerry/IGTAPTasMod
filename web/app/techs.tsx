"use client";

import { useEffect, useState } from "react";
import "./techs.css";

export type TechCategory = "glitchless" | "nmg" | "major";
export type TechOrigin = "community" | "game" | "ours";
export type TechRequires = "NONE" | "WJ" | "WJ_DS" | "WJ_DS_DJ" | "WJ_DS_DJ_BS" | "OMNI";
export type Tech = { id: string; name: string; category: TechCategory; requires: TechRequires; origin: TechOrigin; confirmed: boolean; omniDash?: boolean; floatingOrigin?: boolean; fastForward?: number; description: string; note?: string | null; detector?: string | null;
  related: string[]; start?: string | null; routeId?: string | null; savestate: boolean; ticks: number };
export type TechSeen = { detector: string; label: string; start: number; end: number; input: number };
export type TechState = { list: Tech[]; detectors: { id: string; label: string }[];
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
  [false, "Unconfirmed", "No demo route yet: add one (Use selected route, Demo from last playback) to confirm it, or merge or delete it."],
];
const ORIGINS: [TechOrigin, string, string][] = [
  ["community", "Community", "From the community's tech list"],
  ["game", "Game", "Named in the game's own code"],
  ["ours", "Ours", "Named or found in this repo: check it isn't a community tech under another name"],
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
    <p className="lede">The game plugin doesn&apos;t report techs (an older version, or not connected).</p></div>;

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
    <p className="lede">Every named tech, each with a demo route that performs it. Playing any route marks the techs the overlay detects, and each
      detected tech without a demo gets one from that playback: a savestate captured 25 ticks before the tech, attached to the demo, then the inputs
      up to 25 ticks after it. Playing an older demo that has no savestate gives it one the same way. <b>Use selected route</b> takes a route you
      recorded or wrote, with a copy of its savestate. Renaming a tech renames its in-game overlay label too. <span className="originBadge ours">OURS</span> marks the names we gave:
      if one is a community tech under another name, merge them.</p>

    <div className="techToolbar">
      <input className="techSearch" value={query} placeholder="Search techs" onChange={e => setQuery(e.target.value)} />
      <label className={`capabilityLock executable ${oursOnly ? "on" : ""}`}><input type="checkbox" checked={oursOnly} onChange={e => setOursOnly(e.target.checked)} /><strong>Only ours</strong></label>
      <div className="techAdd">
        <input value={newName} placeholder="New tech name" onChange={e => setNewName(e.target.value)} />
        <select value={newRequires} onChange={e => setNewRequires(e.target.value as TechRequires)}>{REQUIRES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
        <select value={newCategory} onChange={e => setNewCategory(e.target.value as TechCategory)}>{CATEGORIES.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select>
        <button className="secondary" disabled={!newName.trim()} onClick={async () => { if (await onCommand("/techs/add", { name: newName.trim(), category: newCategory, requires: newRequires })) setNewName(""); }}>+ Add tech</button>
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
        {!group.length && <p className="muted">None{q || oursOnly ? " match" : ""}.</p>}
      </div>;
    })}
  </div>;
}

function TechCard({tech, byId, techs, running, selectedRoute, editing, focused, onEdit, onFocus, onCommand, onEditInputs}: {tech: Tech; byId: Map<string, Tech>; techs: TechState; running: boolean;
  selectedRoute?: {id: string; name: string}; editing: boolean; focused: boolean; onEdit: () => void; onFocus: (id: string) => void; onCommand: Command; onEditInputs: (routeId: string) => void}) {
  const seen = techs.seen;
  const detectorLabel = tech.detector ? techs.detectors.find(d => d.id === tech.detector)?.label ?? tech.detector : null;
  const playedDemo = seen && tech.routeId && seen.routeId === tech.routeId && !seen.running;
  const sawIt = !!tech.detector && !!seen?.items.some(s => s.detector === tech.detector);
  const firstSeen = tech.detector ? seen?.items.findIndex(s => s.detector === tech.detector) ?? -1 : -1;
  const related = tech.related.map(id => byId.get(id)).filter((t): t is Tech => !!t);
  const relatedBy = techs.list.filter(t => t.id !== tech.id && t.related.includes(tech.id) && !tech.related.includes(t.id));
  return <article id={`tech-${tech.id}`} className={`techCard ${tech.category} ${tech.origin} ${focused ? "focused" : ""}`}
    title={CATEGORIES.find(c => c[0] === tech.category)?.[1]}>
    <div className="techHead">
      <strong>{tech.name}</strong>
      <span className={`originBadge ${tech.origin}`} title={ORIGINS.find(o => o[0] === tech.origin)?.[2]}>{ORIGINS.find(o => o[0] === tech.origin)?.[1].toUpperCase()}</span>
      {detectorLabel && <span className="detectorChip" title={`Detected in playback (${tech.detector}); the overlay labels it ${tech.name.toUpperCase()}`}>detected</span>}
    </div>
    {tech.description && <p className="techDescription">{tech.description}</p>}
    {tech.note && <p className="techNote">{tech.note}</p>}
    {(related.length > 0 || relatedBy.length > 0) && <div className={`techRelated ${tech.origin === "ours" ? "merge" : ""}`}>
      <span>{tech.origin === "ours" ? "Same as?" : "Related"}</span>
      {[...related, ...relatedBy].map(r => <button key={r.id} className={`relatedChip ${r.origin}`} onClick={() => onFocus(r.id)} title={`${r.name} (${r.origin})`}>{r.name}</button>)}
    </div>}
    <div className="techDemo">
      {tech.routeId
        ? <span className="demoInfo" title={tech.savestate ? `Starts at its own savestate${tech.start ? `, with ${tech.start}'s unlocks when that is a course category` : ""}`
            : tech.start ? `Starts as ${tech.start} (course quick restart and unlocks, or the Full-TAS run's new game)` : "Starts at the quick restart where you are"}>
            Demo · {tech.ticks} t · {(tech.ticks * TICK_SECONDS).toFixed(2)}s{tech.savestate ? " · from its savestate" : tech.start ? ` · from ${tech.start}` : ""}{tech.fastForward ? `, turbo to input ${tech.fastForward}` : ""}{tech.omniDash ? " · + omnidash" : ""}{tech.floatingOrigin ? " · + origin overlay" : ""}</span>
        : <span className="demoInfo missing">No demo yet</span>}
      {playedDemo && tech.detector && <span className={`verdict ${sawIt ? "good" : "bad"}`}>{sawIt ? "✓ performed in the last play" : "✗ not detected in the last play"}</span>}
      <div className="buttonRow">
        <button className="secondary" disabled={!tech.routeId || running} onClick={() => onCommand("/techs/play", { id: tech.id })}>▶ Play</button>
        <button className="secondary" disabled={!tech.routeId || running} title="Game time slowed 10×" onClick={() => onCommand("/techs/play", { id: tech.id, walk: true })}>⏵ Walk</button>
        {firstSeen >= 0 && !tech.routeId && <button className="secondary" disabled={running} onClick={() => onCommand("/techs/demo", { id: tech.id, from: "playback", seen: firstSeen })}>Demo from last playback</button>}
        <button className="secondary" onClick={onEdit}>{editing ? "Close" : "Edit"}</button>
      </div>
    </div>
    {editing && <TechEditor tech={tech} techs={techs} running={running} selectedRoute={selectedRoute} onCommand={onCommand} onEditInputs={onEditInputs} onClose={onEdit} />}
  </article>;
}

function TechEditor({tech, techs, running, selectedRoute, onCommand, onEditInputs, onClose}: {tech: Tech; techs: TechState; running: boolean; selectedRoute?: {id: string; name: string};
  onCommand: Command; onEditInputs: (routeId: string) => void; onClose: () => void}) {
  const [draft, setDraft] = useState({ name: tech.name, category: tech.category, requires: tech.requires, origin: tech.origin, detector: tech.detector ?? "", start: tech.start ?? "",
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
      <label className="field" title="The demo draws the FloatingOrigin sphere, coordinates and lastHyperY in game, and always plays from its start (a savestate doesn't hold the world's origin)">
        <span>FloatingOrigin demo</span><input type="checkbox" checked={draft.floatingOrigin} onChange={e => set({ floatingOrigin: e.target.checked })} /></label>
      <label className="field" title="A Full-TAS demo's first input ticks run in turbo after the new game loads, so it starts where the tech begins (the world as the run left it)">
        <span>Fast-forward (input ticks in turbo)</span><input type="number" min={0} value={draft.fastForward} onChange={e => set({ fastForward: Math.max(0, Number(e.target.value) || 0) })} /></label>
    </div>
    <label className="field"><span>Description</span><textarea value={draft.description} onChange={e => set({ description: e.target.value })} /></label>
    <label className="field"><span>Note</span><textarea value={draft.note} onChange={e => set({ note: e.target.value })} /></label>
    <div className="field"><span>Same as / related</span><div className="techRelated">
      {draft.related.map(id => <span key={id} className="relatedChip">{techs.list.find(t => t.id === id)?.name ?? id}<button onClick={() => set({ related: draft.related.filter(r => r !== id) })}>×</button></span>)}
      <select value="" onChange={e => e.target.value && set({ related: [...draft.related, e.target.value] })}><option value="">+ Add…</option>
        {others.filter(t => !draft.related.includes(t.id)).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
    </div></div>
    <div className="buttonRow">
      <button className="primary" disabled={!draft.name.trim()} onClick={async () => { if (await onCommand("/techs/update", { id: tech.id, ...draft })) onClose(); }}>Save</button>
    </div>

    <p className="eyebrow techEditorHead">DEMO</p>
    <div className="buttonRow">
      <button className="secondary" disabled={running || !selectedRoute || selectedRoute.id === tech.routeId} title={selectedRoute ? `The whole route, from its own start: ${selectedRoute.name}` : "Select a route in the sidebar"}
        onClick={() => onCommand("/techs/demo", { id: tech.id, from: "selected" })}>Use selected route{selectedRoute && selectedRoute.id !== tech.routeId ? ` (${selectedRoute.name})` : ""}</button>
      {seen && seen.items.length > 0 && <>
        <select value={cut} onChange={e => setCut(Number(e.target.value))}><option value={-1}>Cut last playback after…</option>
          {seen.items.map((s, i) => <option key={i} value={i}>{s.label} · tick {s.end}</option>)}</select>
        <button className="secondary" disabled={running || cut < 0} onClick={() => onCommand("/techs/demo", { id: tech.id, from: "playback", seen: cut })}>Cut</button>
      </>}
      <button className="secondary" disabled={!tech.routeId} onClick={() => tech.routeId && onEditInputs(tech.routeId)}>Edit inputs</button>
      <button className="danger" disabled={running || !tech.routeId} onClick={() => window.confirm(`Delete ${tech.name}'s demo route?`) && onCommand("/techs/demo", { id: tech.id, from: "none" })}>Remove demo</button>
    </div>

    <p className="eyebrow techEditorHead">MERGE OR DELETE</p>
    <div className="buttonRow">
      <select value={into} onChange={e => setInto(e.target.value)}><option value="">Merge into…</option>{others.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select>
      <button className="secondary" disabled={!into || running} title="This tech's text goes into the other one, and its detector and demo fill what the other lacks; then this tech is gone"
        onClick={() => window.confirm(`Merge ${tech.name} into ${techs.list.find(t => t.id === into)?.name}?`) && onCommand("/techs/merge", { id: tech.id, into })}>Merge</button>
      <button className="danger" disabled={running} onClick={() => window.confirm(`Delete ${tech.name}${tech.routeId ? " and its demo" : ""}?`) && onCommand("/techs/delete", { id: tech.id })}>Delete tech</button>
    </div>
  </div>;
}
