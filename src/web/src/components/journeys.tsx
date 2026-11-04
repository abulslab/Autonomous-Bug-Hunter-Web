"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useState } from "react";
import { Badge, Code, Panel, Stat, Table, Tone, fmtDate } from "@/components/ui";
import { useApi } from "@/lib/api";

const tone: Record<string, Tone> = { proven: "success", flaky: "warning", failing: "danger", candidate: "accent", planned: "muted" };

// The journey library: happy paths mined from hunts and proven by replay without a model; the planned ones are features
// in the twin that no hunt has reached yet.
export function JourneysView({ project }: { project: string }) {
  const data = useApi<any>(`/projects/${project}/journeys`, { refetch: 15000 });
  const [open, setOpen] = useState<string | null>(null);
  if (!data.data) return null;
  const { journeys, planned } = data.data;
  const shown = journeys.find((j: any) => j.id === open);
  return (
    <div className="space-y-5">
      <Panel title="Journeys" subtitle="Mined from hunts; proven only after two replays in fresh sessions, without a model, reached the same outcome." flush>
        <Table head={["Journey", "Role", "Outcome", "Steps", "Status", "Last runs", "Stories", ""]} empty="No journeys yet. Every project hunt mines the outcomes it reached."
          rows={journeys.map((j: any) => [
            j.name, j.role, <span key="o" className="font-mono text-xs">{j.outcome}</span>, j.steps,
            <Badge key="s" tone={tone[j.status] ?? "neutral"}>{j.status}</Badge>,
            <span key="r" className="text-xs text-slate-500" title={j.runs.map((r: any) => `${fmtDate(r.at)} ${r.passed ? "passed" : "failed"}${r.problem ? `: ${r.problem}` : ""}`).join("\n")}>
              {j.runs.slice(0, 5).map((r: any) => (r.passed ? "✓" : "✗")).join(" ") || "—"}</span>,
            j.stories.join(", ") || "—",
            j.spec ? <button key="t" className="text-xs text-blue-700 hover:underline" onClick={() => setOpen(open === j.id ? null : j.id)}>{open === j.id ? "Hide test" : "Playwright test"}</button> : "",
          ])} />
      </Panel>
      {shown?.spec && <Panel title={`Playwright test: ${shown.name}`}><Code className="max-h-96">{shown.spec}</Code></Panel>}
      {planned.length > 0 && (
        <Panel title="Planned" subtitle="Features the twin knows that write something, which no journey reaches yet." flush>
          <Table head={["Feature", "For role", "Outcome", "Stories"]} rows={planned.map((j: any) => [j.name, j.role || "—", <span key="o" className="font-mono text-xs">{j.outcome}</span>, j.stories.join(", ") || "—"])} />
        </Panel>
      )}
    </div>
  );
}

export function CoverageView({ project }: { project: string }) {
  const data = useApi<any>(`/projects/${project}/coverage`, { refetch: 15000 });
  if (!data.data) return null;
  const c = data.data;
  return (
    <div className="space-y-5">
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Stories covered" value={`${c.covered} / ${c.total}`} hint="by a proven journey or a rule" />
        {c.roles.map((r: any) => <Stat key={r.role} label={`Proven journeys: ${r.role}`} value={`${r.proven} / ${r.journeys}`} />)}
      </div>
      <Panel title="Stories" flush>
        <Table head={["Story", "Version", "Features", "Rules", "Proven journeys", "Other journeys", "Covered"]}
          rows={c.stories.map((s: any) => [
            <span key="k"><span className="font-mono text-xs text-slate-500">{s.key}</span> {s.title}</span>, `v${s.version}`,
            s.features.join(", ") || "—", s.rules.length || "—", s.provenJourneys.join(", ") || "—", s.otherJourneys.join(", ") || "—",
            <Badge key="c" tone={s.covered ? "success" : "warning"}>{s.covered ? "yes" : "no"}</Badge>,
          ])} />
      </Panel>
    </div>
  );
}
