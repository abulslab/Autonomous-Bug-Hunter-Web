"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { Badge, PageHeader, Panel, Spinner, Table } from "@/components/ui";
import { useApi } from "@/lib/api";

const source = (s: any) => (s?.kind === "jira" ? `Jira project ${s.key}` : s?.kind === "confluence" ? `Confluence space ${s.key}` : s?.kind === "files" ? `Files: ${s.path}` : "None");

function ProjectGrounding({ p }: { p: any }) {
  const k = useApi<any>(p.knowledgeVersion ? `/projects/${p.id}/knowledge` : null);
  const learning = useApi<any>(`/projects/${p.id}/learning`);
  return (
    <Panel title={<Link className="text-blue-700 hover:underline" href={`/projects/${p.id}`}>{p.name}</Link>}
      subtitle={p.knowledgeVersion ? `Knowledge version ${p.knowledgeVersion}; every hunt records the version of each story it was grounded in.` : "Not synced yet: open the project and sync its knowledge."}>
      <dl className="mb-3 grid gap-2 text-sm sm:grid-cols-4">
        <div><dt className="text-xs text-slate-500">Stories</dt><dd>{source(p.knowledge.stories)}</dd></div>
        <div><dt className="text-xs text-slate-500">Documents</dt><dd>{source(p.knowledge.documents)}</dd></div>
        <div><dt className="text-xs text-slate-500">Skill packs always applied</dt><dd>{p.skillPacks.join(", ") || "none"}</dd></div>
        <div><dt className="text-xs text-slate-500">Accepted rules (reused without AI)</dt><dd>{learning.data?.library.length ?? "—"}</dd></div>
      </dl>
      {k.data && (
        <Table head={["Story", "Version", "Acceptance criteria"]}
          rows={k.data.stories.map((s: any) => [<span key="k"><span className="font-mono text-xs text-slate-500">{s.key}</span> {s.title}</span>,
            <Badge key="v" tone={s.version > 1 ? "accent" : "muted"}>v{s.version}</Badge>,
            <span key="c" className="text-xs text-slate-600">{((n: number) => `${n} ${n === 1 ? "criterion" : "criteria"}`)(s.criteria.split("\n").filter(Boolean).length)}</span>])} />
      )}
      {k.data?.documents.length > 0 && <p className="mt-2 text-xs text-slate-500">Documents: {k.data.documents.map((d: any) => `${d.title} (v${d.version})`).join("; ")}</p>}
    </Panel>
  );
}

// What each project's models are grounded in: the stories and documents of its latest knowledge version (pulled from
// Jira and Confluence, or files), the skill packs it always applies, and the rules reviewers have accepted. Edit the
// sources where they live; Bug Hunter versions what it pulls.
export default function GroundingPage() {
  const projects = useApi<any[]>("/projects");
  return (
    <>
      <PageHeader title="Grounding"
        subtitle="Expectations come from intent, never from what the app happens to do. This is the intent each project's models are given: versioned stories and documents, skill packs, and rules people have accepted." />
      {!projects.data ? <Spinner /> : <div className="space-y-5">{projects.data.map((p) => <ProjectGrounding key={p.id} p={p} />)}</div>}
    </>
  );
}
