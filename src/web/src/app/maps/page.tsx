"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useState } from "react";
import { Badge, ErrorBox, PageHeader, Panel, Spinner, Table, fmtDate } from "@/components/ui";
import { StateFlowGraph } from "@/components/flow-graph";
import { useApi } from "@/lib/api";

export default function MapsPage() {
  const maps = useApi<any[]>("/hunter/maps");
  const [site, setSite] = useState<string | null>(null);
  const current = site ?? maps.data?.[0]?.site ?? null;
  const detail = useApi<any>(current ? `/hunter/maps/detail?site=${encodeURIComponent(current)}` : null);

  if (maps.error) return <ErrorBox error={maps.error} />;
  if (!maps.data) return <Spinner />;
  const m = detail.data;

  return (
    <>
      <PageHeader title="App maps" subtitle="What Bug Hunter has learned about each site across hunts: states, the routes between them, the API it observed and the intents from the last warm-up. Later hunts reuse it and survey less." />
      {maps.data.length === 0 ? <Panel><p className="text-sm text-slate-500">No maps yet. Every hunt builds one.</p></Panel> : (
        <div className="grid gap-5 lg:grid-cols-[260px_1fr]">
          <Panel title="Sites" flush>
            <ul className="divide-y divide-slate-100">
              {maps.data.map((x) => (
                <li key={x.site}>
                  <button onClick={() => setSite(x.site)} className={`block w-full px-4 py-3 text-left ${x.site === current ? "bg-blue-50/60" : "hover:bg-slate-50"}`}>
                    <div className="truncate font-mono text-xs text-slate-800">{x.site}</div>
                    <div className="mt-0.5 text-xs text-slate-500">{x.states} states · {x.roads} roads · {x.apis} APIs · {x.hunts} hunts</div>
                  </button>
                </li>
              ))}
            </ul>
          </Panel>
          {!m ? <Spinner /> : (
            <div className="space-y-5">
              <Panel title={m.model?.domain ?? "No app model yet"} subtitle={`Updated ${fmtDate(m.updatedAt)} after ${m.hunts} hunts`}>
                {m.model ? (
                  <div className="grid gap-4 text-sm md:grid-cols-3">
                    {[["Entities", m.model.entities], ["Workflows", m.model.workflows], ["Risks", m.model.risks]].map(([label, items]: any) => (
                      <div key={label}><div className="text-xs font-medium text-slate-500">{label}</div><ul className="mt-1 list-disc space-y-0.5 pl-5 text-slate-700">{items.map((x: string, i: number) => <li key={i}>{x}</li>)}</ul></div>
                    ))}
                  </div>
                ) : <p className="text-sm text-slate-500">A Guided hunt writes the app model during warm-up.</p>}
              </Panel>
              {m.intents?.length > 0 && (
                <Panel title={`Intents (${m.intents.length})`} subtitle="From the last warm-up." flush>
                  <Table head={["Intent", "Where", "Priority", "Story"]} rows={m.intents.map((i: any) => [i.goal, <span key="s" className="font-mono text-xs">{i.state}</span>, i.priority, i.requirement ?? "—"])} />
                </Panel>
              )}
              {m.states?.length > 0 && (
                <StateFlowGraph states={m.states} roads={m.roads ?? []} />
              )}
              <Panel title={`States (${m.states.length})`} flush>
                <Table head={["State", "Title", "Visits", "Offers"]} rows={[...m.states].sort((a: any, b: any) => b.visits - a.visits).map((s: any) => [
                  <span key="s" className="font-mono text-xs">{s.id}</span>, s.title, s.visits, <span key="o" className="text-xs text-slate-500">{s.landmarks.slice(0, 8).join(", ")}</span>,
                ])} />
              </Panel>
              <Panel title={`API observed (${m.apis.length})`} subtitle="Built from recorded traffic, no specification needed." flush>
                <Table head={["Endpoint", "Statuses", "Request fields", "Response fields"]} rows={m.apis.map((a: any) => [
                  <span key="e" className="font-mono text-xs">{a.endpoint}</span>,
                  <span key="s" className="space-x-1">{a.statuses.map((x: number) => <Badge key={x} tone={x >= 500 ? "danger" : x >= 400 ? "warning" : "success"}>{x}</Badge>)}</span>,
                  <span key="q" className="font-mono text-xs text-slate-500">{a.requestFields.join(", ") || "—"}</span>,
                  <span key="r" className="font-mono text-xs text-slate-500">{a.responseFields.slice(0, 10).join(", ") || "—"}</span>,
                ])} />
              </Panel>
              <Panel title={`Known routes (${m.roads.length})`} subtitle="Recorded action groups that move between states; the navigator drives these with no AI." flush>
                <Table head={["From", "To", "Actions", "Worked / failed"]} rows={m.roads.slice(0, 60).map((r: any) => [
                  <span key="f" className="font-mono text-xs">{r.from}</span>, <span key="t" className="font-mono text-xs">{r.to}</span>,
                  <span key="a" className="text-xs text-slate-600">{r.signature}</span>, <span key="o" className="tabular-nums">{r.ok} / {r.failed}</span>,
                ])} />
              </Panel>
            </div>
          )}
        </div>
      )}
    </>
  );
}
