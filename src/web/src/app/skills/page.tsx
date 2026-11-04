"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useState } from "react";
import { Badge, Button, Code, ErrorBox, Field, Input, PageHeader, Panel, Spinner, Textarea } from "@/components/ui";
import { api, useAction, useApi } from "@/lib/api";

const TEMPLATE = `## Risks
- One risk per line, specific enough to test.

## Rules
- On payment writes: \`status >= 400 || amount > 0\`

## Probe values
- amount: 0, -1, 0.001, 999999999`;

// Skill packs: domain testing knowledge the planner is given when a pack's keywords appear in the app. Built-in packs
// are files in skills/ (reviewed with the code); custom packs are stored by the API and used by every later hunt.
export default function SkillsPage() {
  const q = useApi<any[]>("/skills");
  const [form, setForm] = useState<{ name: string; title: string; match: string; body: string } | null>(null);
  const keys = ["/skills"];
  const save = useAction(() => api<any>(`/skills/${form!.name.trim()}`, {
    method: "PUT", json: { title: form!.title, match: form!.match.split(","), body: form!.body },
  }), keys);
  const remove = useAction((name: string) => api<any>(`/skills/${name}`, { method: "DELETE" }), keys);

  return (
    <>
      <PageHeader title="Skill packs"
        subtitle="Domain testing knowledge: the risks a careful tester looks for, rules that must hold and values worth trying. A pack is given to the planner when at least two of its keywords appear in the app; its rules are suggestions that the engine still validates against recorded traffic."
        actions={<Button onClick={() => setForm(form ? null : { name: "", title: "", match: "", body: TEMPLATE })}>{form ? "Cancel" : "Add a skill pack"}</Button>} />

      {form && (
        <Panel className="mb-5" title="New skill pack" subtitle="Stored by the API next to the hunt data, so every later hunt can use it without it being committed with the code.">
          <div className="space-y-3">
            <div className="grid gap-3 sm:grid-cols-3">
              <Field label="Name" hint="lower-case id, e.g. payments"><Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></Field>
              <Field label="Title"><Input value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} placeholder="Card payments" /></Field>
              <Field label="Match keywords" hint="comma-separated; two must appear in the app"><Input value={form.match} onChange={(e) => setForm({ ...form, match: e.target.value })} placeholder="card, payment, refund" /></Field>
            </div>
            <Field label="Body (Markdown)"><Textarea rows={10} className="font-mono text-xs" value={form.body} onChange={(e) => setForm({ ...form, body: e.target.value })} /></Field>
            <div className="flex justify-end"><Button disabled={save.isPending} onClick={() => save.mutate(undefined as never, { onSuccess: () => setForm(null) })}>Save</Button></div>
            <ErrorBox error={save.error} />
          </div>
        </Panel>
      )}

      <ErrorBox error={q.error ?? remove.error} />
      {!q.data ? <Spinner /> : (
        <div className="grid gap-5 md:grid-cols-2">
          {q.data.map((p) => (
            <Panel key={p.name}
              title={<span className="flex items-center gap-2">{p.title || p.name}<Badge tone={p.custom ? "accent" : "muted"}>{p.custom ? "custom" : "built-in"}</Badge></span>}
              subtitle={<span>Applies on: {p.match.join(", ")}</span>}
              actions={p.custom ? <Button variant="ghost" size="sm" disabled={remove.isPending} onClick={() => remove.mutate(p.name)}>Delete</Button> : undefined}>
              <Code className="max-h-72 whitespace-pre-wrap text-xs">{p.body}</Code>
            </Panel>
          ))}
        </div>
      )}
    </>
  );
}
