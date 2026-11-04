"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { Badge, Button, ErrorBox, Panel, Table, fmtDate } from "@/components/ui";
import { api, useAction, useApi } from "@/lib/api";

function Chips({ label, items }: { label: string; items: string[] }) {
  if (!items?.length) return null;
  return (
    <div className="mb-2">
      <span className="mr-2 text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</span>
      {items.map((x) => <span key={x} className="mb-1 mr-1 inline-block rounded bg-slate-100 px-1.5 py-0.5 font-mono text-xs text-slate-700">{x}</span>)}
    </div>
  );
}

// What changed and what the project has learned: the change a targeted hunt tests first, the rule library reused
// without AI, stale and retired rules, triage outcomes and the priorities they set.
export function LearningView({ project }: { project: string }) {
  const q = useApi<any>(`/projects/${project}/learning`, { refetch: 20000 });
  const hunt = useAction(() => api<any>(`/projects/${project}/hunts/changed`, { method: "POST", json: {} }), [`/projects/${project}/learning`, `/projects/${project}/runs`, `/projects/${project}/hunts`]);
  if (!q.data) return null;
  const d = q.data; const c = d.pending;
  const nothing = !c.stories.length && !c.newAreas.length;
  return (
    <div className="space-y-5">
      <Panel title="What changed" subtitle="Since the last change-driven hunt: changed stories traced to the features, rules, journeys and endpoints they touch, and areas new in the app twin since the baseline."
        actions={<Button size="sm" disabled={hunt.isPending || nothing} onClick={() => hunt.mutate(undefined as never)}>{hunt.isPending ? "Starting…" : "Hunt the change first"}</Button>}>
        {nothing ? <p className="text-sm text-slate-600">No changes waiting.</p> : (
          <>
            <Chips label="Stories" items={c.stories} />
            <Chips label="Features" items={c.features} />
            <Chips label="Endpoints" items={c.endpoints} />
            <Chips label="Rules gone stale" items={c.rules} />
            <Chips label="Journeys to prove again" items={c.journeys} />
            <Chips label="New areas" items={c.newAreas} />
          </>)}
        {hunt.data && <p className="mt-2 text-sm text-slate-600">Started <Link className="font-mono text-blue-700 hover:underline" href={`/hunts/${hunt.data.id}`}>{hunt.data.id}</Link>: {hunt.data.mission}</p>}
        <ErrorBox error={hunt.error} />
      </Panel>
      <Panel title="Rule library" subtitle="Accepted rules, checked in every project hunt without AI." flush>
        <Table head={["Rule", "On", "Story", "Hunts", "Held", "Violated"]} empty="No accepted rules yet. Accept rules in the Review tab."
          rows={d.library.map((r: any) => [r.statement, <span key="o" className="font-mono text-xs">{r.on}</span>, r.story ?? "—", r.hunts, r.holds, r.violations ? <Badge key="v" tone="danger">{r.violations}</Badge> : 0])} />
      </Panel>
      {(d.stale.length > 0 || d.retired.length > 0) && (
        <Panel title="Stale and retired rules" subtitle="Stale: its story changed, so it is not checked until understanding rewrites it and a reviewer accepts it. Retired: rejected in review, never proposed again." flush>
          <Table head={["Rule", "State", "Why"]}
            rows={[...d.stale.map((r: any) => [<span key="r" className="font-mono text-xs">{r.on} {r.check}</span>, <Badge key="s" tone="warning">stale</Badge>, `story ${r.story} changed`]),
              ...d.retired.map((r: any) => [<span key="r" className="font-mono text-xs">{r.rule}</span>, <Badge key="s" tone="muted">retired</Badge>, r.reason ?? "rejected in review"])]} />
        </Panel>)}
      <Panel title="Triage outcomes" subtitle={`Kept by finding, so they apply in later runs. ${d.suppressed} suppressed (not a bug, won't fix). Priorities: ${d.priorities.join(", ") || "none yet"}.`} flush>
        <Table head={["Finding", "Outcome", "Endpoint", "Issue", "By", "When"]} empty="No triage yet. Triage findings on a project hunt's page."
          rows={d.triage.map((t: any) => [t.title, <Badge key="o" tone={t.outcome === "bug" ? "danger" : t.outcome === "known-issue" ? "warning" : "muted"}>{t.outcome}</Badge>,
            <span key="e" className="font-mono text-xs">{t.endpoint ?? "—"}</span>, t.issueKey ?? "—", t.by, <span key="w" className="whitespace-nowrap text-xs">{fmtDate(t.at)}</span>])} />
      </Panel>
    </div>
  );
}
