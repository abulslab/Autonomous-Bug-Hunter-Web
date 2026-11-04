"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useState } from "react";
import { Badge, Button, ErrorBox, Panel, Table, fmtDate } from "@/components/ui";
import { api, useAction, useApi } from "@/lib/api";

function List({ title, items, tone }: { title: string; items: React.ReactNode[]; tone?: string }) {
  if (!items.length) return null;
  return (
    <div>
      <h4 className={`mb-1 text-xs font-semibold uppercase tracking-wide ${tone ?? "text-slate-500"}`}>{title} ({items.length})</h4>
      <ul className="mb-3 list-disc space-y-0.5 pl-5 text-sm text-slate-700">{items.map((x, i) => <li key={i}>{x}</li>)}</ul>
    </div>
  );
}

// Runs and the report a QE lead reads first: what changed since the baseline (the last accepted run).
export function RunsView({ project }: { project: string }) {
  const runs = useApi<any[]>(`/projects/${project}/runs`, { refetch: 15000 });
  const [picked, setPicked] = useState<string | null>(null);
  const current = picked ?? runs.data?.find((r) => r.finished)?.id ?? null;
  const detail = useApi<any>(current ? `/projects/${project}/runs/${current}` : null);
  const accept = useAction(() => api<any>(`/projects/${project}/runs/${current}/accept`, { method: "POST" }), [`/projects/${project}/runs`, `/projects/${project}/runs/${current}`]);
  if (!runs.data) return null;
  const c = detail.data?.comparison;
  return (
    <div className="space-y-5">
      <Panel title="Runs" subtitle="The hunts started together: a survey of every role, or one hunt. The baseline is the run later ones are compared with." flush>
        <Table head={["Run", "Kind", "Hunts", "Started", "Proven findings", "Proven journeys", ""]} empty="No runs yet. Start a hunt or a survey from this project."
          rows={runs.data.map((r) => [
            <button key="r" className={`font-mono text-xs ${r.id === current ? "font-semibold text-slate-900" : "text-blue-700 hover:underline"}`} onClick={() => setPicked(r.id)}>{r.id}</button>,
            r.kind, <span key="h" className="space-x-1">{r.hunts.map((h: string) => <Link key={h} className="font-mono text-xs text-blue-700 hover:underline" href={`/hunts/${h}`}>{h}</Link>)}</span>,
            <span key="s" className="whitespace-nowrap text-xs">{fmtDate(r.startedAt)}</span>, r.findings ?? "—", r.journeysProven ?? "—",
            r.baseline ? <Badge key="b" tone="success">baseline</Badge> : r.finished ? <Badge key="b" tone="muted">finished</Badge> : <Badge key="b" tone="accent">running</Badge>,
          ])} />
      </Panel>
      {detail.data?.finished && (
        <Panel title={`What changed since the baseline${c?.baselineRun ? ` (${c.baselineRun})` : ""}`}
          subtitle="Behaviour that changed without a story change is a change signal for review, not a bug: expectations come from the stories."
          actions={!detail.data.isBaseline && <Button size="sm" variant="secondary" disabled={accept.isPending} onClick={() => accept.mutate(undefined as never)}>Accept as baseline</Button>}>
          <ErrorBox error={accept.error} />
          {detail.data.isBaseline ? <p className="text-sm text-slate-600">This run is the baseline.</p> : !c || c.empty ? <p className="text-sm text-slate-600">Nothing changed.</p> : (
            <div className="grid gap-x-8 md:grid-cols-2">
              <div>
                <List title="New findings" tone="text-red-700" items={c.findingsNew.map((f: any) => `${f.title} (${f.provenance}, ${f.reproduced ?? "not replayed"})`)} />
                <List title="Regressed journeys" tone="text-red-700" items={c.journeysRegressed} />
                <List title="Behaviour changed, no story change" tone="text-amber-700" items={c.behaviourChanged.filter((b: any) => !b.explainedBy.length).map((b: any) => <span key={b.endpoint}><span className="font-mono text-xs">{b.endpoint}</span>: {b.before} → {b.after}</span>)} />
                <List title="Coverage lost" tone="text-amber-700" items={c.coverageLost} />
              </div>
              <div>
                <List title="Fixed" tone="text-green-700" items={c.findingsFixed.map((f: any) => f.title)} />
                <List title="Recovered journeys" tone="text-green-700" items={c.journeysRecovered} />
                <List title="New journeys" items={c.journeysNew} />
                <List title="Stories changed" items={c.storiesChanged} />
                <List title="Behaviour changed with a story change" items={c.behaviourChanged.filter((b: any) => b.explainedBy.length).map((b: any) => `${b.endpoint} (${b.explainedBy.join(", ")})`)} />
                <List title="Endpoints added" items={c.endpointsAdded} />
                <List title="Endpoints removed" items={c.endpointsRemoved} />
                <List title="Coverage gained" items={c.coverageGained} />
                <List title="Still open" items={c.findingsStillOpen.map((f: any) => f.title)} />
                <List title="Not retested (this run never tried where they show)" tone="text-amber-700" items={(c.findingsNotRetested ?? []).map((f: any) => `${f.title} (${f.role ?? "default"})`)} />
              </div>
            </div>
          )}
        </Panel>
      )}
    </div>
  );
}
