"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useEffect, useRef, useState } from "react";
import { Badge, Button, Code, Panel, statusTone, useToast } from "@/components/ui";
import { API, api, getToken } from "@/lib/api";

const clip = (v?: string) => (v && v.length > 60 ? `${v.slice(0, 60)}… (${v.length} chars)` : v ?? "");

// One line per recorded step, in plain words.
export function describe(a: any, t: any) {
  const el = t ? `${t.role} "${t.name}"` : a.ref != null ? `[${a.ref}]` : "";
  switch (a.type) {
    case "open": return `Open ${a.url}`;
    case "fill": return `Fill ${el} with "${clip(a.value)}"`;
    case "select": return `Select "${a.value}" in ${el}`;
    case "check": return `${a.value === "false" ? "Uncheck" : "Check"} ${el}`;
    case "press": return `Press ${a.key ?? "Enter"}${t ? ` in ${el}` : ""}`;
    case "navigate": return `Go to ${a.url}`;
    case "request": return `API ${a.key} ${a.args?.path ?? ""}: ${a.args?.label ?? ""}`;
    case "click": return `Click ${el}`;
    default: return a.type;
  }
}

export const driverLabel: Record<string, string> = { Heuristic: "Sweep", Model: "AI", Routine: "Routine", Probe: "Probe", Navigator: "Navigator" };

function useShot(hunt: string, n: number) {
  const [src, setSrc] = useState<string | null>(null);
  useEffect(() => {
    let url: string | null = null;
    setSrc(null);
    const token = getToken();
    fetch(`${API}/api/hunter/hunts/${hunt}/shots/${n}`, { headers: token ? { "x-api-key": token } : {} })
      .then((r) => (r.ok ? r.blob() : null))
      .then((b) => { if (b) { url = URL.createObjectURL(b); setSrc(url); } })
      .catch(() => {});
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [hunt, n]);
  return src;
}

// Full-page screenshot: the thumbnail shows the top of the page; clicking opens the whole page.
export function Shot({ hunt, n, onOpen }: { hunt: string; n: number; onOpen?: () => void }) {
  const src = useShot(hunt, n);
  if (!src) return <div className="flex aspect-[16/10] w-full items-center justify-center rounded-md border border-slate-200 bg-slate-50 text-xs text-slate-400">No screenshot</div>;
  return (
    <button type="button" onClick={onOpen} className="block w-full overflow-hidden rounded-md border border-slate-200 hover:border-slate-400" title="View the full page">
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src={src} alt={`Page after step ${n}`} className="aspect-[16/10] w-full object-cover object-top" />
    </button>
  );
}

export function ShotViewer({ hunt, steps, n, onChange, onClose }: { hunt: string; steps: any[]; n: number; onChange: (n: number) => void; onClose: () => void }) {
  const src = useShot(hunt, n);
  const withShots = steps.filter((s) => s.hasShot).map((s) => s.n);
  const i = withShots.indexOf(n);
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
      if (e.key === "ArrowLeft" && i > 0) onChange(withShots[i - 1]);
      if (e.key === "ArrowRight" && i >= 0 && i < withShots.length - 1) onChange(withShots[i + 1]);
    };
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  });
  const step = steps.find((s) => s.n === n);
  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-white" role="dialog" aria-label="Full page screenshot">
      <div className="flex items-center gap-3 border-b border-slate-200 px-5 py-3 text-sm">
        <span className="font-semibold">Page after step {n}</span>
        {step && <span className="truncate text-slate-500">{describe(step.action, step.target)}</span>}
        <span className="ml-auto flex items-center gap-2">
          <Button variant="secondary" size="sm" disabled={i <= 0} onClick={() => onChange(withShots[i - 1])}>Previous</Button>
          <Button variant="secondary" size="sm" disabled={i < 0 || i >= withShots.length - 1} onClick={() => onChange(withShots[i + 1])}>Next</Button>
          <Button variant="secondary" size="sm" onClick={onClose}>Close</Button>
        </span>
      </div>
      <div className="flex-1 overflow-auto bg-slate-50 p-5">
        {/* eslint-disable-next-line @next/next/no-img-element */}
        {src ? <img src={src} alt={`Full page after step ${n}`} className="mx-auto max-w-none border border-slate-200 bg-white" /> : <p className="p-8 text-center text-sm text-slate-500">Loading…</p>}
      </div>
    </div>
  );
}

// The live view while a hunt runs (the real browser, cursor and clicks included); the recording once it has finished.
export function LiveMonitor({ h, active }: { h: any; active: boolean }) {
  const [media, setMedia] = useState<{ live: string | null; video: string | null } | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const retry = active && !media?.live ? h.steps.length : 0;
  useEffect(() => {
    api(`/hunter/hunts/${h.id}/ticket`, { method: "POST" }).then(setMedia).catch(() => setMedia(null));
  }, [h.id, active, h.hasVideo, retry]);
  const last = h.steps[h.steps.length - 1];
  if (!active && !media?.video) return null;
  return (
    <Panel
      title={<span className="flex items-center gap-2">{active ? <Badge tone="danger">Live</Badge> : <Badge>Recording</Badge>}{active ? "The hunter's browser" : "Recording of the hunt"}</span>}
      subtitle={active && last ? `Step ${last.n}: ${describe(last.action, last.target)}` : undefined}
      actions={<Button variant="secondary" size="sm" onClick={() => box.current?.requestFullscreen?.()}>Full screen</Button>}
    >
      <div ref={box} className="flex items-center justify-center overflow-hidden rounded-md border border-slate-200 bg-slate-100">
        {active && media?.live ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img key={media.live} src={`${API}${media.live}`} alt="Live view of the hunter's browser" className="max-h-[70vh] w-full object-contain" />
        ) : active ? (
          <p className="py-24 text-sm text-slate-500">Opening the browser…</p>
        ) : (
          <video key={media!.video!} src={`${API}${media!.video}`} controls className="max-h-[70vh] w-full" />
        )}
      </div>
    </Panel>
  );
}

const provenance: Record<string, { label: string; tone: "success" | "accent" | "warning"; note: string }> = {
  Observed: { label: "Proven by an oracle", tone: "success", note: "A built-in check fired on recorded evidence." },
  Invariant: { label: "Rule broken", tone: "accent", note: "Recorded evidence broke a rule written from the requirements. The evidence is real; read the rule before filing." },
  Judged: { label: "AI suspicion", tone: "warning", note: "The model's opinion. No check confirmed it; triage before filing." },
};

// shot: the step whose page to show; a probe has no page of its own, so the caller passes the last page before it.
export function FindingCard({ f, hunt, shot, onStep, onOpenShot, github, project }: { f: any; hunt: string; shot?: number; onStep: (n: number) => void; onOpenShot: (n: number) => void; github?: boolean; project?: string }) {
  const toast = useToast();
  const [triage, setTriage] = useState<string>("");
  const [jira, setJira] = useState<string | null>(null);
  const fileJira = async () => {
    try { const r = await api<any>(`/projects/${project}/findings/${hunt}/${f.id}/jira`, { method: "POST" }); setJira(r.key); toast.show(r.created ? `Filed ${r.key}` : `Commented on ${r.key}`); }
    catch (e: any) { toast.show(e.message); }
  };
  const triageAs = async (outcome: string) => {
    setTriage(outcome);
    if (!outcome || !project) return;
    try { await api<any>(`/projects/${project}/triage`, { method: "POST", json: { hunt, finding: f.id, outcome } }); toast.show(`Triaged as ${outcome}`); }
    catch (e: any) { toast.show(e.message); }
  };
  const [issue, setIssue] = useState<string | null>(f.issueUrl ?? null);
  const [filing, setFiling] = useState(false);
  const file = async () => {
    setFiling(true);
    try { const r = await api<any>(`/hunter/hunts/${hunt}/findings/${f.id}/issue`, { method: "POST" }); setIssue(r.url); toast.show(r.created ? "Issue created" : "Added to the existing issue"); }
    catch (e: any) { toast.show(e.message); }
    finally { setFiling(false); }
  };
  const p = provenance[f.provenance] ?? provenance.Judged;
  return (
    <article className="rounded-lg border border-slate-200 bg-white">
      <header className="flex flex-wrap items-center gap-2 border-b border-slate-100 px-5 py-3">
        <Badge tone={f.severity === "High" || f.severity === "Critical" ? "danger" : f.severity === "Medium" ? "warning" : "neutral"}>{f.severity}</Badge>
        <Badge tone={p.tone} title={p.note}>{p.label}</Badge>
        {f.reproduced && <Badge tone={statusTone(f.reproduced)}>{f.reproduced === "Confirmed" ? `Reproduced ${f.replayPasses}/${f.replayRuns}` : f.reproduced === "Flaky" ? `Flaky ${f.replayPasses}/${f.replayRuns}` : "Not reproduced"}</Badge>}
        {f.minimalSteps && <Badge tone="success" title="Repro reduced by delta debugging">Repro: {f.minimalSteps.length} of {f.trailSteps} steps</Badge>}
        {f.requirement && <Badge tone="neutral">{f.requirement}</Badge>}
        <h3 className="min-w-0 flex-1 text-sm font-semibold text-slate-900">{f.title}</h3>
        {issue ? <a className="text-xs font-medium text-blue-700 hover:underline" href={issue} target="_blank" rel="noreferrer">GitHub issue</a>
          : github && <Button variant="secondary" size="sm" disabled={filing} onClick={file}>{filing ? "Filing…" : "File GitHub issue"}</Button>}
        {project && (jira ? <span className="text-xs font-medium text-slate-700">Jira {jira}</span>
          : <Button variant="secondary" size="sm" onClick={fileJira} title="File as a Jira bug linked to the story it breaks">File in Jira</Button>)}
        {project && (
          <select aria-label="Triage" title="Triage outcome, remembered for this bug in later runs" className="h-8 rounded-md border border-slate-300 bg-white px-2 text-xs text-slate-700" value={triage} onChange={(e) => triageAs(e.target.value)}>
            <option value="">Triage…</option><option value="bug">Bug</option><option value="not-a-bug">Not a bug</option><option value="known-issue">Known issue</option><option value="wont-fix">Won&apos;t fix</option>
          </select>)}
        <span className="font-mono text-xs text-slate-400">{f.id}</span>
      </header>
      <div className="grid gap-5 p-5 md:grid-cols-[1fr_280px]">
        <div className="space-y-3 text-sm">
          <dl className="grid grid-cols-[80px_1fr] gap-x-3 gap-y-1.5">
            <dt className="text-slate-500">Expected</dt><dd className="text-slate-800">{f.expected}</dd>
            <dt className="text-slate-500">Actual</dt><dd className="text-slate-800">{f.actual}</dd>
            <dt className="text-slate-500">Found</dt>
            <dd className="text-slate-600">
              <button className="font-medium text-blue-700 hover:underline" onClick={() => onStep(f.step)}>step {f.step}</button> on <span className="font-mono text-xs">{f.url}</span>
            </dd>
          </dl>
          {f.minimalSteps && (
            <div className="flex items-center gap-2 rounded-md border border-emerald-200 bg-emerald-50/70 px-3 py-2 text-xs text-emerald-900">
              <span className="font-semibold text-emerald-800">Delta Debugging:</span>
              <span>Repro shrunk from {f.trailSteps} steps to {f.minimalSteps.length} minimal actions ({Math.max(1, Math.round((1 - f.minimalSteps.length / (f.trailSteps || 1)) * 100))}% noise eliminated without losing reproduction).</span>
            </div>
          )}
          {f.confirmedBy?.length > 0 && <p className="text-xs text-slate-500">Found again independently: {f.confirmedBy.join(", ")}.</p>}
          {f.reproduced === "NotReproduced" && <p className="text-xs text-amber-800">Did not happen again in {f.replayRuns} replays without AI; it may depend on state the replay did not recreate. Treat as unconfirmed.</p>}
          {f.evidence && (
            <details><summary className="cursor-pointer text-xs font-medium text-slate-600">Recorded evidence</summary><Code className="mt-2 max-h-48">{f.evidence}</Code></details>
          )}
          <details>
            <summary className="cursor-pointer text-xs font-medium text-slate-600">Steps to reproduce ({f.repro.length}{f.minimalSteps ? ", minimal" : ""})</summary>
            <ol className="mt-2 list-decimal space-y-0.5 pl-5 text-xs text-slate-700">{f.repro.map((r: string, i: number) => <li key={i}>{r}</li>)}</ol>
          </details>
          {f.spec && (
            <details>
              <summary className="cursor-pointer text-xs font-medium text-slate-600">Playwright test</summary>
              <div className="mt-2 flex justify-end"><Button variant="ghost" size="sm" onClick={() => navigator.clipboard.writeText(f.spec).then(() => toast.show("Copied"))}>Copy</Button></div>
              <Code className="max-h-80">{f.spec}</Code>
            </details>
          )}
        </div>
        <div>
          <Shot hunt={hunt} n={shot ?? f.step} onOpen={() => onOpenShot(shot ?? f.step)} />
          {shot != null && shot !== f.step && <p className="mt-1.5 text-xs text-slate-500">Page at step {shot}, before the API request that showed the bug.</p>}
        </div>
      </div>
      {toast.node}
    </article>
  );
}
