"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { use, useState } from "react";
import { Badge, Button, ErrorBox, PageHeader, Panel, Spinner, Stat, Table, Tabs, fmtDate, fmtDuration, fmtUsd, statusTone } from "@/components/ui";
import { FindingCard, LiveMonitor, Shot, ShotViewer, describe, driverLabel } from "@/components/hunt";
import { ReplayInspector } from "@/components/replay-inspector";
import { api, download, useAction, useApi } from "@/lib/api";

const intentNote: Record<string, string> = {
  Held: "Its success checks were evaluated and held.",
  Violated: "A success check broke: see the findings.",
  Exercised: "Actions ran, but no success check applied to what happened.",
  Unreachable: "The navigator found no known route to it.",
  Skipped: "The step budget ran out first.",
  Failed: "Nothing could be run there.",
};

export default function HuntPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const { data, error } = useApi<any>(`/hunter/hunts/${id}`, { refetch: 2500 });
  const status = useApi<any>("/hunter/status");
  const [exportAs, setExportAs] = useState("");
  const cancel = useAction(async () => api(`/hunter/hunts/${id}/cancel`, { method: "POST" }), ["/hunter"]);
  const [tab, setTab] = useState("overview");
  const [selected, setSelected] = useState<number | null>(null);
  const [zoom, setZoom] = useState<number | null>(null);

  if (error) return <ErrorBox error={error} />;
  if (!data) return <Spinner />;
  const h = data.hunt;
  const confirmed = h.findings.filter((f: any) => f.reproduced === "Confirmed");
  const proven = h.findings.filter((f: any) => f.provenance !== "Judged");
  const order = (f: any) => (f.reproduced === "Confirmed" ? 0 : f.provenance === "Judged" ? 3 : f.reproduced ? 2 : 1);
  const findings = [...h.findings].sort((a: any, b: any) => order(a) - order(b));
  const steps = h.steps.filter((s: any) => s.n > 0);
  const shown = selected ?? h.steps[h.steps.length - 1]?.n ?? 0;
  const goTo = (n: number) => { setSelected(n); setTab("inspector"); };
  const intents: any[] = h.intents ?? [];

  return (
    <>
      <PageHeader
        title={<span className="flex items-center gap-3">Hunt <span className="font-mono">{h.id}</span><Badge tone={statusTone(h.status)}>{h.status}</Badge></span>}
        subtitle={<span className="font-mono text-xs">{h.url}</span>}
        actions={<>
          <select aria-label="Export" value={exportAs} className="h-9 rounded-md border border-slate-300 bg-white px-2 text-sm text-slate-700"
            onChange={(e) => { const f = e.target.value; setExportAs(""); if (f) download(`/hunter/hunts/${id}/report?format=${f}`, `${id}.${f === "junit" ? "xml" : f === "sarif" ? "sarif" : f}`).catch(() => {}); }}>
            <option value="">Export…</option>
            <option value="md">Markdown report</option>
            <option value="html">HTML report</option>
            <option value="sarif">SARIF (code scanning)</option>
            <option value="junit">JUnit XML (CI)</option>
          </select>
          <Link href="/"><Button variant="secondary">All hunts</Button></Link>
          {data.active && <Button variant="danger" disabled={cancel.isPending} onClick={() => cancel.mutate(undefined as never)}>Stop hunt</Button>}
        </>}
      />
      {h.error && <div className="mb-5"><ErrorBox error={new Error(h.error)} /></div>}

      <div className="mb-6 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <Stat label="Confirmed bugs" value={confirmed.length} hint={`${proven.length} proven, ${h.findings.length - proven.length} suspicions`} />
        <Stat label="Intents" value={intents.length ? `${intents.filter((i) => i.status === "Held").length} / ${intents.length}` : "—"}
          hint={intents.length ? `held; ${intents.filter((i) => i.status === "Violated").length} violated` : "Guided hunts only"} />
        <Stat label="Steps" value={`${steps.filter((s: any) => s.driver !== "Probe").length} / ${h.maxSteps}`} hint={`+ ${h.probeRequests ?? 0} probe requests`} />
        <Stat label="AI cost" value={fmtUsd(h.usage.costUsd)} hint={h.usage.costPerConfirmedBug != null ? `${fmtUsd(h.usage.costPerConfirmedBug)} per confirmed bug` : `${h.usage.modelCalls} model calls`} />
        <Stat label="Models" value={<span className="text-base">{h.policy === "Heuristic" ? "None" : h.model?.model ?? "—"}</span>} hint={h.plannerModel ? `planner ${h.plannerModel.model}` : h.strategy} />
        <Stat label="Duration" value={<span className="text-base">{fmtDuration(h.startedAt, h.finishedAt)}</span>} hint={fmtDate(h.createdAt)} />
      </div>

      <Tabs value={tab} onChange={setTab} tabs={[
        { id: "overview", label: "Overview" },
        { id: "inspector", label: "Split-View Inspector" },
        { id: "findings", label: `Findings (${h.findings.length})` },
        { id: "intents", label: `Intents (${intents.length})` },
        { id: "rules", label: `Rules (${h.invariants.length})` },
        { id: "timeline", label: `Timeline (${steps.length})` },
        { id: "coverage", label: "Coverage" },
      ]} />

      {tab === "overview" && (
        <div className="space-y-5">
          <LiveMonitor h={h} active={data.active} />
          <div className="grid gap-5 lg:grid-cols-2">
            <Panel title="What the AI understood" subtitle={h.skillPacks?.length ? `Skill packs used: ${h.skillPacks.join(", ")}` : undefined}>
              {h.appModel ? (
                <div className="space-y-3 text-sm">
                  <p className="text-slate-800">{h.appModel.domain}</p>
                  {[["Entities", h.appModel.entities], ["Workflows", h.appModel.workflows], ["Risks", h.appModel.risks]].map(([label, items]: any) => items?.length > 0 && (
                    <div key={label}>
                      <div className="text-xs font-medium text-slate-500">{label}</div>
                      <ul className="mt-1 list-disc space-y-0.5 pl-5 text-slate-700">{items.map((x: string, i: number) => <li key={i}>{x}</li>)}</ul>
                    </div>
                  ))}
                </div>
              ) : <p className="text-sm text-slate-500">No app model: only Guided hunts build one.</p>}
            </Panel>
            <Panel title="Hunt log" subtitle="What the engine did and decided, in order.">
              <ul className="max-h-96 space-y-2 overflow-auto text-sm text-slate-700">{h.notes.map((n: string, i: number) => <li key={i} className="border-l-2 border-slate-200 pl-3">{n}</li>)}</ul>
            </Panel>
          </div>
          {h.benchmark && (
            <Panel title={`Benchmark: ${h.benchmark.detected} of ${h.benchmark.total} seeded defects found`}
              subtitle={`${h.benchmark.confirmedDetected ?? 0} confirmed on replay. ${h.benchmark.unmatchedObserved} proven findings not explained by a seeded defect (false-positive candidates).`} flush>
              <Table head={["Seeded defect", "Result", "Finding"]} rows={h.benchmark.seeded.map((d: any) => [
                d.title,
                <Badge key="r" tone={d.findingId ? "success" : "muted"}>{d.findingId ? (d.via === "rule" ? "Found by a rule" : "Found") : "Not found"}</Badge>,
                <span key="f" className="font-mono text-xs">{d.findingId ?? "—"}</span>,
              ])} />
            </Panel>
          )}
        </div>
      )}

      {tab === "findings" && (
        <div className="space-y-4">
          {findings.length === 0
            ? <Panel><p className="text-sm text-slate-500">{data.active ? "Hunting…" : "No findings. Nothing here was proven broken."}</p></Panel>
            : findings.map((f: any) => <FindingCard key={f.id} f={f} hunt={h.id} shot={h.steps.filter((s: any) => s.hasShot && s.n <= f.step).at(-1)?.n} onStep={goTo} onOpenShot={setZoom} github={status.data?.github?.enabled} project={h.projectId ?? undefined} />)}
        </div>
      )}

      {tab === "intents" && (
        intents.length === 0 ? <Panel><p className="text-sm text-slate-500">Intents come from the warm-up of a Guided hunt.</p></Panel> : (
          <div className="space-y-4">
            {intents.map((i: any) => (
              <Panel key={i.id} title={<span className="flex flex-wrap items-center gap-2"><span className="font-mono text-xs text-slate-400">{i.id}</span>{i.goal}<Badge tone={statusTone(i.status)} title={intentNote[i.status]}>{i.status}</Badge>{i.requirement && <Badge>{i.requirement}</Badge>}<Badge tone="muted">priority {i.priority}</Badge></span>}
                subtitle={<span>{i.actor && `${i.actor} · `}<span className="font-mono">{i.state}</span> · drove {i.routeSteps} steps{i.reroutes ? `, rerouted ${i.reroutes}x` : ""} · tested {i.testSteps}{i.findingIds?.length ? ` · findings ${i.findingIds.join(", ")}` : ""}</span>}>
                <div className="grid gap-5 md:grid-cols-2">
                  <div className="text-sm">
                    <div className="text-xs font-medium text-slate-500">Success means</div>
                    <ul className="mt-1 list-disc space-y-0.5 pl-5 text-slate-700">{i.success.map((x: string, k: number) => <li key={k}>{x}</li>)}</ul>
                    {i.ruleIds?.length > 0 && <p className="mt-2 text-xs text-slate-500">Checked by {i.ruleIds.length} rule{i.ruleIds.length === 1 ? "" : "s"} on every response.</p>}
                  </div>
                  <div className="text-sm">
                    <div className="text-xs font-medium text-slate-500">Variants</div>
                    <ul className="mt-1 space-y-1.5">{i.variants.map((v: any, k: number) => (
                      <li key={k} className="flex flex-wrap items-center gap-2">
                        <Badge tone={statusTone(v.status)}>{v.status}</Badge>
                        <span className="text-slate-700">{v.name}</span>
                        {v.routine && <Badge tone="muted">{v.routine}</Badge>}
                        {v.findingIds?.length > 0 && <span className="font-mono text-xs text-red-700">{v.findingIds.join(", ")}</span>}
                        {v.problem && <span className="block w-full text-xs text-amber-800">{v.problem}</span>}
                      </li>
                    ))}</ul>
                  </div>
                </div>
              </Panel>
            ))}
          </div>
        )
      )}

      {tab === "rules" && (
        <Panel title="Rules checked on every API response" subtitle="Written from your stories and the API by the planner (or by you), admitted only after they parse and evaluate on recorded traffic." flush>
          <Table head={["Rule", "Applies to", "From", "Result"]} empty="No rules in this hunt." rows={h.invariants.map((r: any) => [
            <div key="r"><div className="text-slate-800">{r.claim}</div><div className="font-mono text-xs text-slate-500">{r.check}</div></div>,
            <span key="o" className="font-mono text-xs">{r.on}</span>,
            <span key="f" className="text-xs">{r.intent ?? r.requirement ?? (r.source === "User" ? "you" : "—")}</span>,
            <span key="s" className="whitespace-nowrap"><Badge tone={statusTone(r.status)}>{r.status === "Violated" ? "Broken" : r.status === "Holding" ? "Holds" : "Not exercised"}</Badge>{r.checks > 0 && <span className="ml-1.5 text-xs text-slate-500">{r.holds}/{r.checks}</span>}</span>,
          ])} />
        </Panel>
      )}

      {tab === "inspector" && (
        <ReplayInspector hunt={h} initialStep={shown} />
      )}

      {tab === "timeline" && (
        <div className="grid gap-5 lg:grid-cols-[1fr_380px]">
          <Panel title="Every step, newest first" flush>
            <div className="max-h-[70vh] divide-y divide-slate-100 overflow-auto">
              {[...h.steps].reverse().map((s: any) => (
                <button key={s.n} onClick={() => setSelected(s.n)} className={`block w-full px-5 py-2.5 text-left text-sm ${s.n === shown ? "bg-blue-50/60" : "hover:bg-slate-50"}`}>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="w-8 shrink-0 font-mono text-xs text-slate-400">#{s.n}</span>
                    {s.driver && <Badge tone="muted">{driverLabel[s.driver] ?? s.driver}{s.routine ? ` · ${s.routine}` : ""}</Badge>}
                    <span className="min-w-0 break-words text-slate-800">{describe(s.action, s.target)}</span>
                    {!s.ok && <Badge tone={s.blocked ? "warning" : "danger"}>{s.blocked ? "Blocked" : "Error"}</Badge>}
                    {s.findingIds.map((fid: string) => <Badge key={fid} tone="danger">{fid}</Badge>)}
                  </div>
                  {s.reasoning && <div className="mt-1 pl-10 text-xs text-slate-500">{s.reasoning}</div>}
                  {(s.error || s.blocked) && <div className="mt-1 pl-10 text-xs text-amber-800">{s.blocked ?? s.error}</div>}
                  {s.requests.length > 0 && <div className="mt-1 pl-10 font-mono text-[11px] text-slate-400">{s.requests.join(" · ")}</div>}
                </button>
              ))}
            </div>
          </Panel>
          <div className="space-y-3">
            <Panel title={`Page after step ${shown}`}>
              <Shot key={shown} hunt={h.id} n={shown} onOpen={() => setZoom(shown)} />
              <p className="mt-2 truncate font-mono text-xs text-slate-500">{h.steps.find((s: any) => s.n === shown)?.url}</p>
            </Panel>
          </div>
        </div>
      )}

      {tab === "coverage" && (
        <div className="grid gap-5 lg:grid-cols-2">
          <Panel title={`Pages (${h.coverage.pages.length})`}><ul className="space-y-1 font-mono text-xs text-slate-700">{h.coverage.pages.map((p: string) => <li key={p}>{p}</li>)}</ul></Panel>
          <Panel title={`API endpoints exercised (${h.coverage.endpoints.length})`} subtitle={`${h.coverage.formsSubmitted} submissions, ${h.coverage.requests} requests`}>
            <ul className="space-y-1 font-mono text-xs text-slate-700">{h.coverage.endpoints.map((p: string) => <li key={p}>{p}</li>)}</ul>
          </Panel>
        </div>
      )}

      {zoom != null && <ShotViewer hunt={h.id} steps={h.steps} n={zoom} onChange={setZoom} onClose={() => setZoom(null)} />}
    </>
  );
}
