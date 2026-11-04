"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { Badge, ErrorBox, PageHeader, Panel, Spinner, Stat, Table, fmtDate, fmtUsd, statusTone } from "@/components/ui";
import { useApi } from "@/lib/api";

// Ground truth: hunts against the demo apps, whose seeded defects are known, scored for recall, confirmation and
// unexplained findings (false-positive candidates), next to their AI cost.
export default function BenchmarksPage() {
  const hunts = useApi<any[]>("/hunter/hunts", { refetch: 10000 });
  if (hunts.error) return <ErrorBox error={hunts.error} />;
  if (!hunts.data) return <Spinner />;
  const runs = hunts.data.filter((h) => h.benchmark && h.status === "Completed");
  const withDefects = runs.filter((h) => h.benchmark.total > 0);
  const clean = runs.filter((h) => h.benchmark.total === 0);
  const best = withDefects.reduce((b: any, h) => (!b || h.benchmark.confirmedDetected / h.benchmark.total > b.benchmark.confirmedDetected / b.benchmark.total ? h : b), null);
  const fp = clean.reduce((n, h) => n + h.benchmark.unmatchedObserved, 0);

  return (
    <>
      <PageHeader title="Benchmarks"
        subtitle="Every completed hunt against a demo app (the shop or Demo Hotel) is scored against its seeded defects. Runs with every defect switched off measure false positives: any proven finding there is a false alarm." />
      <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Benchmark runs" value={runs.length} hint={`${withDefects.length} with seeded defects, ${clean.length} clean`} />
        <Stat label="Best confirmed recall" value={best ? `${best.benchmark.confirmedDetected} / ${best.benchmark.total}` : "—"} hint={best ? `${best.benchmark.target ?? "Demo shop"}, ${best.strategy}, ${best.maxSteps} steps` : undefined} />
        <Stat label="False positives (clean runs)" value={clean.length ? fp : "—"} hint={clean.length ? `across ${clean.length} runs with every defect off` : "Run with all defects off"} />
        <Stat label="AI spend on benchmarks" value={fmtUsd(runs.reduce((n, h) => n + (h.costUsd ?? 0), 0))} />
      </div>
      <Panel title="Runs" subtitle="Recall counts seeded defects whose finding reproduced on both replays." flush>
        <Table
          head={["Hunt", "App", "Strategy and models", "Steps", "Seeded found", "Confirmed", "Unexplained", "AI cost", "Per confirmed bug", "Date"]}
          empty="No benchmark runs yet: start a hunt with Use Demo shop or Use Demo Hotel."
          rows={runs.map((h) => [
            <Link key="i" className="whitespace-nowrap font-mono text-xs font-medium text-blue-700 hover:underline" href={`/hunts/${h.id}`}>{h.id}</Link>,
            <span key="a" className="whitespace-nowrap">{h.benchmark.target ?? "Demo shop"}</span>,
            <span key="s">{h.strategy}<span className="block text-xs text-slate-500">{h.policy === "Heuristic" ? "no AI" : `${h.model?.model ?? ""}${h.plannerModel ? ` + ${h.plannerModel.model}` : ""}`}</span></span>,
            h.maxSteps,
            h.benchmark.total ? `${h.benchmark.detected} / ${h.benchmark.total}` : <Badge key="c" tone="muted">clean run</Badge>,
            h.benchmark.total ? <Badge key="b" tone={statusTone(h.benchmark.confirmedDetected === h.benchmark.total ? "Confirmed" : "Flaky")}>{h.benchmark.confirmedDetected ?? 0} / {h.benchmark.total}</Badge> : "—",
            <span key="u" className={h.benchmark.unmatchedObserved ? "font-medium text-red-700" : ""}>{h.benchmark.unmatchedObserved}</span>,
            fmtUsd(h.costUsd),
            fmtUsd(h.costPerConfirmedBug),
            <span key="d" className="whitespace-nowrap text-xs text-slate-500">{fmtDate(h.createdAt)}</span>,
          ])}
        />
      </Panel>
    </>
  );
}
