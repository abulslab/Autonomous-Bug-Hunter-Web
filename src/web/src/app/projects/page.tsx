"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { Badge, ErrorBox, PageHeader, Panel, Spinner, Table, fmtDate } from "@/components/ui";
import { useApi } from "@/lib/api";

const source = (s: any) => (s?.kind === "jira" ? `Jira ${s.key}` : s?.kind === "confluence" ? `Confluence ${s.key}` : s?.kind === "files" ? "Files" : "None");

export default function ProjectsPage() {
  const projects = useApi<any[]>("/projects");
  if (projects.error) return <ErrorBox error={projects.error} />;
  if (!projects.data) return <Spinner />;
  return (
    <>
      <PageHeader title="Projects"
        subtitle="An application under test that Bug Hunter lives with across runs: its environments, the roles that use it, and the stories and documents that say what it should do, versioned on every sync." />
      <Panel flush>
        <Table
          head={["Project", "Environments", "Roles", "Stories from", "Documents from", "Knowledge", "Updated"]}
          empty="No projects yet. Create one through the API (POST /api/projects)."
          rows={projects.data.map((p) => [
            <Link key="n" href={`/projects/${p.id}`} className="font-medium text-blue-700 hover:underline">{p.name}<span className="block font-mono text-xs text-slate-500">{p.id}</span></Link>,
            p.environments.map((e: any) => e.name).join(", "),
            p.roles.map((r: any) => r.name).join(", ") || "—",
            source(p.knowledge?.stories),
            source(p.knowledge?.documents),
            p.latestKnowledge ? <Badge key="k" tone="accent">v{p.latestKnowledge.version} · {p.latestKnowledge.stories} stories</Badge> : <Badge key="k" tone="muted">not synced</Badge>,
            <span key="u" className="whitespace-nowrap text-xs text-slate-500">{fmtDate(p.updatedAt)}</span>,
          ])}
        />
      </Panel>
    </>
  );
}
