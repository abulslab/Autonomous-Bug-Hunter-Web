"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useEffect, useState } from "react";
import { Badge, Button, Checkbox, ErrorBox, Field, Input, Panel, Select, Stat } from "@/components/ui";
import { api, useAction, useApi } from "@/lib/api";

const kindLabel: Record<string, string> = { rule: "Rule", journey: "Journey", behaviour: "Behaviour change", baseline: "Baseline update" };

function Item({ project, it }: { project: string; it: any }) {
  const [reason, setReason] = useState("");
  const keys = [`/projects/${project}/review`, `/projects/${project}/runs`, `/projects/${project}/twin`, `/projects/${project}/journeys`];
  const decide = useAction((accept: boolean) => api<any>(`/projects/${project}/review/${it.id}`, { method: "POST", json: { accept, reason } }), keys);
  const d = it.decision;
  return (
    <div className="border-b border-slate-100 px-4 py-3 last:border-0">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone="muted">{kindLabel[it.kind] ?? it.kind}</Badge>
        {it.status === "pending" ? <Badge tone="accent">pending</Badge> : <Badge tone={it.status === "accepted" ? "success" : "danger"}>{it.status}</Badge>}
        {it.blocking && <Badge tone="danger" title="New findings or regressions: only a person decides">blocking</Badge>}
        {it.spotCheck && <Badge tone="warning" title="A judge decision sampled for a person to look at">spot check</Badge>}
      </div>
      <p className="mt-1.5 text-sm text-slate-800">{it.summary}</p>
      <p className="text-xs text-slate-500">Accepting means {it.accepting}.{it.evidence ? ` Evidence: ${it.evidence}.` : ""}</p>
      {it.judge && <p className="mt-1 text-xs text-slate-600"><span className="font-medium">Judge ({it.judge.by.replace("judge:", "")}):</span> {it.judge.accept ? "accept" : "reject"}: {it.judge.reason}</p>}
      {d && <p className="mt-1 text-xs text-slate-600"><span className="font-medium">Decided by {d.by}:</span> {d.reason}</p>}
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <Input className="h-8 max-w-sm text-xs" placeholder="Reason (logged with the decision)" value={reason} onChange={(e) => setReason(e.target.value)} />
        {it.status !== "accepted" && <Button size="sm" disabled={decide.isPending} onClick={() => decide.mutate(true)}>{it.status === "pending" ? "Accept" : "Reverse: accept"}</Button>}
        {it.status !== "rejected" && <Button size="sm" variant="secondary" disabled={decide.isPending} onClick={() => decide.mutate(false)}>{it.status === "pending" ? "Reject" : "Reverse: reject"}</Button>}
      </div>
      <ErrorBox error={decide.error} />
      {it.history?.length > 0 && <p className="mt-1 text-xs text-slate-400">Earlier: {it.history.map((h: any) => `${h.accept ? "accepted" : "rejected"} by ${h.by}`).join("; ")}</p>}
    </div>
  );
}

// The review gate: what a model proposed or the app started doing becomes an expectation only after a decision.
export function ReviewView({ project }: { project: string }) {
  const q = useApi<any>(`/projects/${project}/review`, { refetch: 20000 });
  const status = useApi<any>("/hunter/status");
  const [s, setS] = useState<any>(null);
  const [filter, setFilter] = useState("pending");
  useEffect(() => { if (q.data && !s) setS(q.data.settings); }, [q.data, s]);
  const save = useAction(() => api<any>(`/projects/${project}/review/settings`, { method: "PUT", json: s }), [`/projects/${project}/review`]);
  const judge = useAction(() => api<any>(`/projects/${project}/review/judge`, { method: "POST", json: { model: s?.judgeModel || null } }), [`/projects/${project}/review`, `/projects/${project}/runs`, `/projects/${project}/twin`, `/projects/${project}/journeys`]);
  if (!q.data || !s) return null;
  const a = q.data.agreement;
  const models: any[] = status.data?.models ?? [];
  const items = q.data.items.filter((i: any) => filter === "all" || (filter === "spot" ? i.spotCheck : i.status === filter));
  return (
    <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-4">
        <Stat label="Waiting for review" value={q.data.pending} />
        <Stat label="Spot checks" value={q.data.spotChecks} hint="judge decisions to confirm" />
        <Stat label="Judge agreement" value={a.compared ? `${Math.round(a.rate * 100)}%` : "—"} hint={`${a.agreed} of ${a.compared} decisions people also made`} />
        <Stat label="Planner model" value={<span className="text-sm">{q.data.plannerModel ?? "—"}</span>} hint="the judge must be another family" />
      </div>
      <Panel title="LLM judge" subtitle="The judge reviews against the stories and documents. It decides by itself only when bypass is on, it is from another model family than the planner, and its agreement with people is at or above the threshold. Behaviour changes and blocking items always need a person.">
        <div className="grid gap-3 md:grid-cols-4">
          <Field label="Judge model"><Select value={s.judgeModel ?? ""} onChange={(e) => setS({ ...s, judgeModel: e.target.value || null })}><option value="">None</option>{models.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}</Select></Field>
          <Field label="Agreement threshold"><Input type="number" step={0.05} min={0.5} max={1} value={s.agreementThreshold} onChange={(e) => setS({ ...s, agreementThreshold: Number(e.target.value) })} /></Field>
          <Field label="Decisions to compare first"><Input type="number" min={1} value={s.minAgreementSample} onChange={(e) => setS({ ...s, minAgreementSample: Number(e.target.value) })} /></Field>
          <Field label="Spot-check rate"><Input type="number" step={0.05} min={0} max={1} value={s.spotCheckRate} onChange={(e) => setS({ ...s, spotCheckRate: Number(e.target.value) })} /></Field>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-3">
          <Checkbox checked={s.bypass} onChange={(v) => setS({ ...s, bypass: v })} label="Let the judge decide (bypass)" hint="Otherwise its verdicts are advice, and build the agreement measure." />
          <Button size="sm" variant="secondary" disabled={save.isPending} onClick={() => save.mutate(undefined as never)}>Save settings</Button>
          <Button size="sm" disabled={judge.isPending || !s.judgeModel} onClick={() => judge.mutate(undefined as never)}>{judge.isPending ? "Judging…" : "Ask the judge"}</Button>
        </div>
        {judge.data && <p className="mt-2 text-sm text-slate-600">Judged {judge.data.judged}, decided {judge.data.applied}.{judge.data.notes?.length ? ` ${judge.data.notes.join("; ")}.` : ""}</p>}
        <ErrorBox error={save.error ?? judge.error} />
      </Panel>
      <Panel title="Review queue" flush actions={<Select className="h-8 text-xs" value={filter} onChange={(e) => setFilter(e.target.value)}>
        <option value="pending">Pending</option><option value="spot">Spot checks</option><option value="accepted">Accepted</option><option value="rejected">Rejected</option><option value="all">All</option></Select>}>
        {items.length ? items.map((it: any) => <Item key={it.id} project={project} it={it} />) : <p className="px-4 py-3 text-sm text-slate-500">Nothing here.</p>}
      </Panel>
    </div>
  );
}
