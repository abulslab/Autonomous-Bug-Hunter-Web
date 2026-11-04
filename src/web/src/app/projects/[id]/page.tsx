"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useState } from "react";
import { Badge, Button, Code, ErrorBox, Field, Input, PageHeader, Panel, Select, Spinner, Table, Tabs, fmtDate, statusTone } from "@/components/ui";
import { api, useAction, useApi } from "@/lib/api";
import { TwinView } from "@/components/twin";
import { CoverageView, JourneysView } from "@/components/journeys";
import { RunsView } from "@/components/runs";
import { ReviewView } from "@/components/review";
import { LearningView } from "@/components/learning";
import { ReadinessView } from "@/components/readiness";
import { SpecUploader } from "@/components/spec-uploader";

const source = (s: any) => (s?.kind === "jira" ? `Jira project ${s.key}` : s?.kind === "confluence" ? `Confluence space ${s.key}` : s?.kind === "files" ? `Files: ${s.path}` : "None");

function Diff({ d }: { d: any }) {
  const parts: React.ReactNode[] = [];
  if (d.storiesAdded?.length) parts.push(<li key="sa">Stories added: {d.storiesAdded.join(", ")}</li>);
  if (d.storiesRemoved?.length) parts.push(<li key="sr">Stories removed: {d.storiesRemoved.join(", ")}</li>);
  for (const c of d.storiesChanged ?? []) parts.push(
    <li key={c.key}><span className="font-medium">{c.key}</span> v{c.from} → v{c.to}{c.titleTo ? ` (now "${c.titleTo}")` : ""}
      {c.criteriaAdded.map((l: string) => <div key={"+" + l} className="pl-3 text-green-800">+ {l}</div>)}
      {c.criteriaRemoved.map((l: string) => <div key={"-" + l} className="pl-3 text-red-800">− {l}</div>)}
    </li>);
  for (const c of d.documentsChanged ?? []) parts.push(<li key={c.id}>Page <span className="font-medium">{c.title}</span> v{c.from} → v{c.to}: {c.linesAdded.length} lines added, {c.linesRemoved.length} removed</li>);
  if (d.documentsAdded?.length) parts.push(<li key="da">Pages added: {d.documentsAdded.join(", ")}</li>);
  if (d.documentsRemoved?.length) parts.push(<li key="dr">Pages removed: {d.documentsRemoved.join(", ")}</li>);
  return parts.length ? <ul className="space-y-1 text-xs text-slate-700">{parts}</ul> : <span className="text-xs text-slate-500">First version</span>;
}

export default function ProjectPage() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const project = useApi<any>(`/projects/${id}`);
  const knowledge = useApi<any>(project.data?.knowledgeVersion ? `/projects/${id}/knowledge` : null);
  const history = useApi<any[]>(`/projects/${id}/knowledge/history`);
  const hunts = useApi<any[]>(`/projects/${id}/hunts`, { refetch: 10000 });
  const review = useApi<any>(`/projects/${id}/review`, { refetch: 20000 });
  const status = useApi<any>("/hunter/status");
  const [tab, setTab] = useState("readiness");
  const [run, setRun] = useState({ environment: "", role: "", strategy: "Heuristic", connection: "heuristic", maxSteps: 60 });
  const sync = useAction(() => api<any>(`/projects/${id}/sync`, { method: "POST" }), [`/projects/${id}`, `/projects/${id}/knowledge`, `/projects/${id}/knowledge/history`, "/projects"]);
  const twinKeys = [`/projects/${id}`, `/projects/${id}/twin`, `/projects/${id}/twin/history`, `/projects/${id}/permissions`];
  const importSpecs = useAction(() => api<any>(`/projects/${id}/specs/import`, { method: "POST" }), twinKeys);
  const survey = useAction(() => api<any>(`/projects/${id}/survey`, { method: "POST", json: {} }), [`/projects/${id}/hunts`]);
  const start = useAction(() => api<any>(`/projects/${id}/hunts`, {
    method: "POST",
    json: { environment: run.environment || null, role: run.role || null, strategy: run.strategy, connection: run.strategy === "Heuristic" ? "heuristic" : run.connection, maxSteps: run.maxSteps },
  }), [`/projects/${id}/hunts`]);

  if (project.error) return <ErrorBox error={project.error} />;
  if (!project.data) return <Spinner />;
  const p = project.data;
  const k = knowledge.data;
  const models: any[] = status.data?.models ?? [];

  return (
    <>
      <PageHeader title={p.name} subtitle={p.mission}
        actions={<div className="flex gap-2">
          <Button variant="secondary" disabled={sync.isPending} onClick={() => sync.mutate(undefined as never)}>{sync.isPending ? "Syncing…" : "Sync knowledge"}</Button>
          {p.specs?.length > 0 && <Button variant="secondary" disabled={importSpecs.isPending} onClick={() => importSpecs.mutate(undefined as never)}>{importSpecs.isPending ? "Importing…" : "Import specs"}</Button>}
          <Button variant="secondary" disabled={survey.isPending} onClick={() => survey.mutate(undefined as never)} title="One hunt per role without AI, one after another">{survey.isPending ? "Starting…" : "Survey all roles"}</Button>
        </div>} />
      {importSpecs.data && <p className="-mt-3 mb-4 text-sm text-slate-600">Twin v{importSpecs.data.twinVersion}: {importSpecs.data.imported.map((i: any) => `${i.endpoints ?? i.calls} from ${i.kind}`).join(", ")}; {importSpecs.data.unknowns} unknowns.</p>}
      {survey.data && <p className="-mt-3 mb-4 text-sm text-slate-600">Survey started: {survey.data.hunts.map((h: any) => `${h.role} (${h.hunt})`).join(", ")}. Each role runs after the one before.</p>}
      <ErrorBox error={importSpecs.error ?? survey.error} />
      {sync.data && <p className="-mt-3 mb-4 text-sm text-slate-600">{sync.data.changed ? `Knowledge is now version ${sync.data.version}.` : `No changes since version ${sync.data.version}.`}</p>}
      <ErrorBox error={sync.error} />
      <div className="mb-5"><SpecUploader projectId={id} onImported={() => project.refetch?.()} /></div>

      <div className="mb-5 grid gap-5 lg:grid-cols-3">
        <Panel title="Environments" flush>
          <Table head={["Name", "URL", "Reset hook"]} rows={p.environments.map((e: any) => [e.name, <span key="u" className="font-mono text-xs">{e.url}</span>, e.resetUrl ? <Badge key="r" tone="success">yes</Badge> : "—"])} />
        </Panel>
        <Panel title="Roles" subtitle="Test accounts are project secrets, referenced by name." flush>
          <Table head={["Role", "Account"]} rows={p.roles.map((r: any) => [r.name, Object.keys(r.secrets).length ? Object.values(r.secrets).join(", ") : <span key="n" className="text-slate-500">no sign-in</span>])} />
        </Panel>
        <Panel title="Knowledge sources">
          <dl className="space-y-2 text-sm">
            <div><dt className="text-xs text-slate-500">Stories</dt><dd className="break-all">{source(p.knowledge.stories)}</dd></div>
            <div><dt className="text-xs text-slate-500">Documents</dt><dd className="break-all">{source(p.knowledge.documents)}</dd></div>
            <div><dt className="text-xs text-slate-500">Skill packs always applied</dt><dd>{p.skillPacks.join(", ") || "—"}</dd></div>
            <div><dt className="text-xs text-slate-500">Secrets set</dt><dd className="font-mono text-xs">{p.secrets.join(", ") || "none"}</dd></div>
          </dl>
        </Panel>
      </div>

      <div className="grid gap-5 lg:grid-cols-[1fr_320px]">
        <div>
          <Tabs value={tab} onChange={setTab} tabs={[
            { id: "readiness", label: "Readiness" },
            { id: "runs", label: "Runs" },
            { id: "review", label: `Review${review.data?.pending ? ` (${review.data.pending})` : ""}` },
            { id: "learning", label: "Changes & learning" },
            { id: "app", label: "App model" },
            { id: "journeys", label: "Journeys" },
            { id: "coverage", label: "Coverage" },
            { id: "stories", label: `Stories${k ? ` (${k.stories.length})` : ""}` },
            { id: "documents", label: `Documents${k ? ` (${k.documents.length})` : ""}` },
            { id: "history", label: `History${history.data ? ` (${history.data.length})` : ""}` },
            { id: "hunts", label: `Hunts${hunts.data ? ` (${hunts.data.length})` : ""}` },
          ]} />
          {tab === "readiness" && <ReadinessView project={id} confluence={p.knowledge.documents?.kind === "confluence"} />}
          {tab === "runs" && <RunsView project={id} />}
          {tab === "review" && <ReviewView project={id} />}
          {tab === "learning" && <LearningView project={id} />}
          {tab === "app" && <TwinView project={id} />}
          {tab === "journeys" && <JourneysView project={id} />}
          {tab === "coverage" && <CoverageView project={id} />}
          {tab === "stories" && (k ? (
            <div className="space-y-3">
              {k.stories.map((s: any) => (
                <Panel key={s.key} title={<span className="flex items-center gap-2"><span className="font-mono text-xs text-slate-500">{s.key}</span>{s.title}<Badge tone={s.version > 1 ? "accent" : "muted"}>v{s.version}</Badge></span>}
                  actions={s.url ? <a className="text-xs text-blue-700 hover:underline" href={s.url} target="_blank" rel="noreferrer">Source</a> : undefined}>
                  <ul className="list-disc space-y-0.5 pl-5 text-sm text-slate-700">{s.criteria.split("\n").filter(Boolean).map((l: string) => <li key={l}>{l}</li>)}</ul>
                </Panel>
              ))}
            </div>) : <Panel><p className="text-sm text-slate-500">Not synced yet. Sync to pull the stories and documents.</p></Panel>)}
          {tab === "documents" && k && (
            <div className="space-y-3">{k.documents.map((d: any) => <Panel key={d.id} title={<span className="flex items-center gap-2">{d.title}<Badge tone={d.version > 1 ? "accent" : "muted"}>v{d.version}</Badge></span>}><Code className="max-h-72 whitespace-pre-wrap">{d.text}</Code></Panel>)}</div>)}
          {tab === "history" && (
            <Panel flush><Table head={["Version", "Synced", "Sources", "What changed"]} empty="No syncs yet."
              rows={(history.data ?? []).map((h) => [<Badge key="v" tone="accent">v{h.version}</Badge>, <span key="t" className="whitespace-nowrap text-xs">{fmtDate(h.syncedAt)}</span>, <span key="s" className="text-xs">{h.storySource}; {h.documentSource}</span>, <Diff key="d" d={h.diff} />])} /></Panel>)}
          {tab === "hunts" && (
            <Panel flush><Table head={["Hunt", "Status", "Strategy", "Environment", "Role", "Knowledge", "Confirmed", "Started"]} empty="No hunts for this project yet."
              rows={(hunts.data ?? []).map((h) => [<Link key="i" className="font-mono text-xs text-blue-700 hover:underline" href={`/hunts/${h.id}`}>{h.id}</Link>, <Badge key="s" tone={statusTone(h.status)}>{h.status}</Badge>, h.strategy, h.environment, h.role ?? "—", h.knowledgeVersion ? `v${h.knowledgeVersion}` : "—", h.confirmed, <span key="c" className="whitespace-nowrap text-xs">{fmtDate(h.createdAt)}</span>])} /></Panel>)}
        </div>

        <Panel title="Start a hunt" subtitle="Uses the environment's URL and reset hook, the role's account, and the latest stories and documents (synced first).">
          <div className="space-y-3">
            <Field label="Environment"><Select value={run.environment} onChange={(e) => setRun({ ...run, environment: e.target.value })}>{p.environments.map((e: any) => <option key={e.name} value={e.name}>{e.name}</option>)}</Select></Field>
            <Field label="Role"><Select value={run.role} onChange={(e) => setRun({ ...run, role: e.target.value })}><option value="">Default</option>{p.roles.map((r: any) => <option key={r.id} value={r.id}>{r.name}</option>)}</Select></Field>
            <Field label="Strategy"><Select value={run.strategy} onChange={(e) => setRun({ ...run, strategy: e.target.value })}>{["Heuristic", "Hybrid", "Guided", "Model"].map((s) => <option key={s}>{s}</option>)}</Select></Field>
            {run.strategy !== "Heuristic" && <Field label="Model"><Select value={run.connection} onChange={(e) => setRun({ ...run, connection: e.target.value })}><option value="heuristic" disabled>Choose a model</option>{models.map((m) => <option key={m.id} value={m.id}>{m.label}</option>)}</Select></Field>}
            <Field label="Step budget"><Input type="number" min={5} max={150} value={run.maxSteps} onChange={(e) => setRun({ ...run, maxSteps: Number(e.target.value) })} /></Field>
            <Button disabled={start.isPending} onClick={() => start.mutate(undefined as never, { onSuccess: (r: any) => router.push(`/hunts/${r.id}`) })}>{start.isPending ? "Starting…" : "Start hunt"}</Button>
            <ErrorBox error={start.error} />
          </div>
        </Panel>
      </div>
    </>
  );
}
