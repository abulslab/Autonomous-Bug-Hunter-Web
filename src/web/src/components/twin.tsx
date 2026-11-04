"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useState } from "react";
import { Badge, Button, ErrorBox, Panel, Select, Stat, Table, Tone, fmtDate, fmtUsd } from "@/components/ui";
import { StateFlowGraph } from "./flow-graph";
import { api, useAction, useApi } from "@/lib/api";

const statusTone: Record<string, Tone> = { declared: "neutral", observed: "success", inferred: "warning", verified: "accent", accepted: "success" };
const permTone: Record<string, Tone> = { allowed: "success", denied: "neutral", error: "warning", unknown: "muted" };
const codeTone = (c: number): Tone => (c < 300 ? "success" : c < 500 ? "warning" : "danger");

// The project's app twin: what Bug Hunter knows about the application, how it knows it, and as whom it saw it.
export function TwinView({ project }: { project: string }) {
  const twin = useApi<any>(`/projects/${project}/twin`);
  const history = useApi<any[]>(`/projects/${project}/twin/history`);
  const permissions = useApi<any[]>(`/projects/${project}/permissions`);
  const status = useApi<any>("/hunter/status");
  // Local models first: the default choice should cost nothing.
  const models: { id: string; label: string }[] = [...((status.data?.ollama?.models ?? []) as string[]).map((m) => ({ id: `ollama:${m}`, label: `Ollama (local) ${m}` })), ...(status.data?.models ?? [])];
  const [model, setModel] = useState("");
  const understand = useAction(() => api<any>(`/projects/${project}/understand`, { method: "POST", json: { model: model || models[0]?.id, force: true } }),
    [`/projects/${project}/twin`, `/projects/${project}/twin/history`]);
  if (twin.error) return <Panel><p className="text-sm text-slate-500">No app twin yet. Every hunt started from this project adds to it.</p></Panel>;
  const t = twin.data;
  if (!t) return null;
  return (
    <div className="space-y-5">
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Twin version" value={`v${t.version}`} hint={`from ${t.fromHunt}, ${fmtDate(t.builtAt)}`} />
        <Stat label="Pages" value={t.states.length} hint={`${t.roads.length} recorded routes`} />
        <Stat label="API endpoints" value={t.endpoints.length} />
        <Stat label="Unknowns" value={t.unknowns.length} hint="gaps the next hunt should close" />
      </div>
      <Panel title="Understanding" subtitle="A model proposes entities, features, lifecycles and rules from this twin and the project's knowledge; the verifier keeps only what the recorded or declared evidence supports."
        actions={<div className="flex items-center gap-2">
          <Select className="w-56" value={model} onChange={(e) => setModel(e.target.value)}>{models.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}</Select>
          <Button size="sm" disabled={understand.isPending || models.length === 0} onClick={() => understand.mutate(undefined as never)}>{understand.isPending ? "Understanding…" : "Understand"}</Button>
        </div>}>
        <ErrorBox error={understand.error} />
        {t.understanding ? (
          <p className="text-sm text-slate-600">Last run by <span className="font-mono text-xs">{t.understanding.model}</span>, {fmtDate(t.understanding.at)}: {t.understanding.proposed} proposed, {t.understanding.admitted} kept, {t.rejected.length} refused; {t.understanding.seconds}s, {fmtUsd(t.understanding.costUsd)}.</p>
        ) : <p className="text-sm text-slate-500">Not run yet.</p>}
      </Panel>
      {(t.entities.length > 0 || t.features.length > 0) && (
        <div className="grid gap-5 lg:grid-cols-2">
          <Panel title="Entities" flush>
            <Table head={["Entity", "Status", "Fields"]} rows={t.entities.map((e: any) => [e.name, <Badge key="s" tone={statusTone[e.status] ?? "neutral"}>{e.status}</Badge>, <span key="f" className="text-xs text-slate-600">{e.fields.join(", ")}</span>])} />
          </Panel>
          <Panel title="Features" flush>
            <Table head={["Feature", "Status", "Endpoints and pages", "Stories"]} rows={t.features.map((f: any) => [f.name, <Badge key="s" tone={statusTone[f.status] ?? "neutral"}>{f.status}</Badge>,
              <span key="e" className="font-mono text-xs text-slate-600">{[...f.endpoints, ...f.pages].join(", ")}</span>, f.stories.join(", ") || "—"])} />
          </Panel>
        </div>
      )}
      {t.lifecycles.length > 0 && (
        <Panel title="Lifecycles" flush>
          <Table head={["Entity", "Status", "States", "Transitions"]} rows={t.lifecycles.map((l: any) => [`${l.entity}.${l.field}`, <Badge key="s" tone={statusTone[l.status] ?? "neutral"}>{l.status}</Badge>, l.states.join(" → "),
            <span key="t" className="text-xs text-slate-600">{l.transitions.map((x: any) => `${x.from} → ${x.to} by ${x.endpoint}${x.role ? ` (${x.role})` : ""}`).join("; ")}</span>])} />
        </Panel>
      )}
      {t.rules.length > 0 && (
        <Panel title="Rules" subtitle="Expectations, not yet facts: a reviewer accepts them, and every response is checked against them." flush>
          <Table head={["Rule", "Story", "On", "Check", "Status"]} rows={t.rules.map((r: any) => [r.statement, r.story ?? "—", <span key="o" className="font-mono text-xs">{r.on}</span>,
            <span key="c" className="font-mono text-xs">{r.check}</span>, <Badge key="s" tone={statusTone[r.status] ?? "neutral"}>{r.status}</Badge>])} />
        </Panel>
      )}
      {t.rejected.length > 0 && (
        <Panel title="Refused by the verifier">
          <ul className="space-y-1 text-sm text-slate-700">{t.rejected.map((r: any, i: number) => <li key={i}><Badge tone="muted">{r.kind}</Badge> <span className="font-medium">{r.name}</span>: {r.reason}</li>)}</ul>
        </Panel>
      )}
      {t.unknowns.length > 0 && (
        <Panel title="What is not known yet">
          <ul className="list-disc space-y-0.5 pl-5 text-sm text-slate-700">{t.unknowns.map((u: any) => <li key={u.text}><Badge tone="muted">{u.kind}</Badge> {u.text}</li>)}</ul>
        </Panel>
      )}
      <Panel title="API" subtitle="Every endpoint the browser called, with the statuses seen as each role. no-session: an engine probe resent the call without the session." flush>
        <Table head={["Endpoint", "Status", "Statuses by role", "Fields", "Seen"]}
          rows={t.endpoints.map((e: any) => [
            <span key="e" className="font-mono text-xs" title={e.summary ?? ""}>{e.method} {e.path}{e.declared && <span className="ml-1 text-slate-400">· spec</span>}</span>,
            <Badge key="s" tone={statusTone[e.status] ?? "neutral"}>{e.status}</Badge>,
            <span key="r" className="flex flex-wrap gap-1">{Object.entries(e.statusesByRole).map(([role, codes]: any) => <span key={role} className="text-xs text-slate-600">{role}: {codes.map((c: number) => <Badge key={c} tone={codeTone(c)}>{c}</Badge>)}</span>)}</span>,
            <span key="f" className="text-xs text-slate-500" title={[...e.requestFields.map((f: string) => "request." + f), ...e.responseFields].join("\n")}>{e.requestFields.length} in, {e.responseFields.length} out</span>,
            <span key="v" className="whitespace-nowrap text-xs text-slate-500">v{e.firstSeen}–v{e.lastSeen}</span>,
          ])} />
      </Panel>
      {permissions.data && permissions.data.length > 0 && (
        <Panel title="Permissions" subtitle="What each role got from each endpoint, as observed. Compare with what the stories say each role may do." flush>
          <Table head={["Endpoint", ...Object.keys(permissions.data[0].roles)]}
            rows={permissions.data.map((r) => [
              <span key="e" className="font-mono text-xs">{r.endpoint}</span>,
              ...Object.entries(r.roles).map(([role, v]: any) => <Badge key={role} tone={permTone[v] ?? "muted"}>{v}</Badge>),
            ])} />
        </Panel>
      )}
      {t.states?.length > 0 && (
        <StateFlowGraph states={t.states} roads={t.roads ?? []} />
      )}
      <Panel title="Pages" flush>
        <Table head={["Page", "Title", "Status", "Seen as", "Seen"]}
          rows={t.states.map((s: any) => [
            <span key="i" className="font-mono text-xs">{s.id}</span>, s.title, <Badge key="s" tone={statusTone[s.status] ?? "neutral"}>{s.status}</Badge>, s.roles.join(", "),
            <span key="v" className="whitespace-nowrap text-xs text-slate-500">v{s.firstSeen}–v{s.lastSeen}</span>,
          ])} />
      </Panel>
      <Panel title="Versions" flush>
        <Table head={["Version", "Built", "From hunt", "Knowledge", "Added"]}
          rows={(history.data ?? []).map((v) => [
            <Badge key="v" tone="accent">v{v.version}</Badge>, <span key="b" className="whitespace-nowrap text-xs">{fmtDate(v.builtAt)}</span>, <span key="h" className="font-mono text-xs">{v.fromHunt}</span>,
            v.knowledgeVersion ? `v${v.knowledgeVersion}` : "—",
            <span key="c" className="text-xs text-slate-600">{[
              v.changes.statesAdded.length && `${v.changes.statesAdded.length} pages`, v.changes.endpointsAdded.length && `${v.changes.endpointsAdded.length} endpoints`,
              v.changes.fieldsAdded.length && `${v.changes.fieldsAdded.length} fields`, v.changes.statusesAdded.length && `${v.changes.statusesAdded.length} statuses`,
            ].filter(Boolean).join(", ") || "nothing new"}</span>,
          ])} />
      </Panel>
    </div>
  );
}
