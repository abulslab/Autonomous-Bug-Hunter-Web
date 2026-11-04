"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { Badge, Button, ErrorBox, PageHeader, Panel, Spinner, Stat, Table, fmtDate, fmtDuration, fmtUsd, statusTone } from "@/components/ui";
import { useApi } from "@/lib/api";

const host = (url: string) => { try { return new URL(url).host; } catch { return url; } };

export default function HuntsPage() {
  const hunts = useApi<any[]>("/hunter/hunts", { refetch: 5000 });
  if (hunts.error) return <><PageHeader title="Hunts" /><ErrorBox error={new Error(`The Bug Hunter API is not reachable (${hunts.error.message}). Start it with: dotnet run --project src/BugHunter.Api`)} /></>;
  if (!hunts.data) return <Spinner />;
  const list = hunts.data;
  const confirmed = list.reduce((n, h) => n + (h.confirmed ?? 0), 0);
  const spend = list.reduce((n, h) => n + (h.costUsd ?? 0), 0);
  const bench = list.find((h) => h.benchmark && h.benchmark.total > 0 && h.status === "Completed");

  return (
    <>
      <PageHeader
        title="Hunts"
        subtitle="Each hunt explores an application in a real browser and reports only bugs it can prove: recorded evidence, two replays without AI, and a Playwright test."
        actions={<Link href="/hunts/new"><Button>New hunt</Button></Link>}
      />
      <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Hunts" value={list.length} hint={`${list.filter((h) => h.status === "Completed").length} completed`} />
        <Stat label="Confirmed bugs" value={confirmed} hint="Reproduced on every replay" />
        <Stat label="Cost per confirmed bug" value={confirmed ? fmtUsd(spend / confirmed) : "—"} hint={`${fmtUsd(spend)} AI spend in total`} />
        <Stat label="Latest benchmark" value={bench ? `${bench.benchmark.detected} / ${bench.benchmark.total}` : "—"}
          hint={bench ? `seeded defects found, ${bench.benchmark.unmatchedObserved} unexplained` : "Hunt the demo shop to measure"} />
      </div>
      <Panel title="All hunts" flush>
        <Table
          head={["Hunt", "Target", "Strategy", "Status", "Confirmed", "Intents", "AI cost", "Started", "Duration"]}
          empty={<>No hunts yet. <Link className="font-medium text-blue-700 hover:underline" href="/hunts/new">Start one</Link>.</>}
          rows={list.map((h) => [
            <Link key="id" className="whitespace-nowrap font-mono text-xs font-medium text-blue-700 hover:underline" href={`/hunts/${h.id}`}>{h.id}</Link>,
            <span key="t" className="text-slate-800">{host(h.url)}</span>,
            <span key="s">{h.strategy}{h.model && h.policy !== "Heuristic" && <span className="block text-xs text-slate-500">{h.model.model}{h.plannerModel ? ` + ${h.plannerModel.model}` : ""}</span>}</span>,
            <Badge key="st" tone={statusTone(h.status)}>{h.status}</Badge>,
            <span key="c" className="tabular-nums">{h.confirmed}<span className="text-slate-400"> / {h.observed + h.broken}</span></span>,
            <span key="i" className="tabular-nums">{h.intents ? <>{h.intentsHeld} held{h.intentsViolated ? <span className="text-red-700">, {h.intentsViolated} violated</span> : ""} <span className="text-slate-400">of {h.intents}</span></> : "—"}</span>,
            <span key="cost" className="tabular-nums">{fmtUsd(h.costUsd)}</span>,
            <span key="d" className="whitespace-nowrap text-xs text-slate-500">{fmtDate(h.createdAt)}</span>,
            <span key="du" className="whitespace-nowrap text-xs text-slate-500">{fmtDuration(h.startedAt, h.finishedAt)}</span>,
          ])}
        />
      </Panel>
    </>
  );
}
