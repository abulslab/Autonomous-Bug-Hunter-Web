"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { Badge, Button, ErrorBox, Panel, Stat, Table } from "@/components/ui";
import { API, api, useAction, useApi } from "@/lib/api";

const verdict: Record<string, { label: string; tone: any; note: string }> = {
  ready: { label: "Ready", tone: "success", note: "Nothing blocks the release and nothing is at risk." },
  "at-risk": { label: "At risk", tone: "warning", note: "Nothing blocks the release, but some things are untested, unreviewed or unexplained." },
  "not-ready": { label: "Not ready", tone: "danger", note: "Something blocks the release." },
};

// The release decision, from what is proven: open confirmed bugs, regressed journeys and blocking reviews block it.
export function ReadinessView({ project, confluence }: { project: string; confluence: boolean }) {
  const q = useApi<any>(`/projects/${project}/readiness`, { refetch: 30000 });
  const publish = useAction(() => api<any>(`/projects/${project}/readiness/publish`, { method: "POST" }));
  if (q.error) return <ErrorBox error={q.error} />;
  if (!q.data) return null;
  const r = q.data; const v = verdict[r.verdict] ?? verdict["not-ready"];
  const proven = r.journeys.filter((j: any) => j.status === "proven").length;
  return (
    <div className="space-y-5">
      <Panel title={<span className="flex items-center gap-2">Release readiness <Badge tone={v.tone}>{v.label}</Badge></span>}
        subtitle={`${v.note} Runs ${r.runs?.join(", ") || "none"}, baseline ${r.baseline ?? "none"}, knowledge v${r.knowledgeVersion}, app twin v${r.twinVersion}.`}
        actions={<div className="flex gap-2">
          <a className="inline-flex h-8 items-center rounded-md border border-slate-300 px-3 text-xs font-medium text-slate-700 hover:bg-slate-50" href={`${API}/api/projects/${project}/readiness?format=markdown`} target="_blank" rel="noreferrer">Markdown</a>
          {confluence && <Button size="sm" variant="secondary" disabled={publish.isPending} onClick={() => publish.mutate(undefined as never)}>{publish.isPending ? "Publishing…" : "Publish to Confluence"}</Button>}
        </div>}>
        {publish.data && <p className="mb-2 text-sm text-slate-600">Published as version {publish.data.version} in space {publish.data.space}.</p>}
        <ErrorBox error={publish.error} />
        <div className="grid gap-3 sm:grid-cols-4">
          <Stat label="Open bugs" value={r.openCauses} hint={`${r.openBugs.length} findings; ${r.suppressed} suppressed by triage`} />
          <Stat label="Stories covered" value={`${r.storiesCovered}/${r.stories.length}`} />
          <Stat label="Journeys passing" value={`${proven}/${r.journeys.length}`} />
          <Stat label="Waiting for review" value={r.pendingReviews} />
        </div>
        {(r.blockers.length > 0 || r.risks.length > 0) && (
          <ul className="mt-4 space-y-1 text-sm">
            {r.blockers.map((b: string) => <li key={b} className="text-red-800">Blocker: {b}</li>)}
            {r.risks.map((b: string) => <li key={b} className="text-amber-800">Risk: {b}</li>)}
          </ul>)}
      </Panel>
      <Panel title="Open bugs" flush>
        <Table head={["Bug", "Severity", "Source", "Replay", "Triage", "Issue", "Hunt"]} empty="No open bugs in the latest run."
          rows={r.openBugs.map((b: any) => [<span key="t">{b.title}{!b.retested && <Badge tone="warning">not retested</Badge>}</span>, b.severity, b.provenance, b.reproduced ?? "not replayed", b.triage ?? "untriaged", b.issue ?? "—",
            <Link key="h" className="font-mono text-xs text-blue-700 hover:underline" href={`/hunts/${b.hunt}`}>{b.hunt}</Link>])} />
      </Panel>
      <Panel title="Story coverage" subtitle="A story is covered when a proven journey or an accepted rule checks it." flush>
        <Table head={["Story", "Version", "Covered", "Proven journeys", "Rules"]}
          rows={r.stories.map((s: any) => [<span key="s"><span className="font-mono text-xs text-slate-500">{s.key}</span> {s.title}</span>, `v${s.version}`,
            s.covered ? <Badge key="c" tone="success">yes</Badge> : <Badge key="c" tone="muted">no</Badge>, s.provenJourneys.join(", ") || "—", s.rules.length])} />
      </Panel>
    </div>
  );
}
