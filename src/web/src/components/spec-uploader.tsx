"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useRef, useState } from "react";
import { Badge, Button, useToast } from "@/components/ui";
import { API, getToken } from "@/lib/api";

export function SpecUploader({ projectId, onImported }: { projectId: string; onImported?: (data: any) => void }) {
  const toast = useToast();
  const fileInput = useRef<HTMLInputElement>(null);
  const [loading, setLoading] = useState(false);
  const [dragOver, setDragOver] = useState(false);
  const [summary, setSummary] = useState<string | null>(null);

  const uploadFile = async (file: File) => {
    if (!file.name.endsWith(".json") && !file.name.endsWith(".har")) {
      toast.show("Please upload a JSON OpenAPI specification or .har file.");
      return;
    }

    setLoading(true);
    setSummary(null);
    try {
      const text = await file.text();
      // Validate that it parses as JSON
      JSON.parse(text);

      const token = getToken();
      const res = await fetch(`${API}/api/projects/${projectId}/specs/import?name=${encodeURIComponent(file.name)}`, {
        method: "POST",
        headers: {
          "content-type": "application/json",
          ...(token ? { "x-api-key": token } : {}),
        },
        body: text,
      });

      const data = await res.json();
      if (!res.ok) {
        throw new Error(data.detail ?? data.title ?? `Upload failed (${res.status})`);
      }

      const importedKinds = (data.imported || [])
        .map((i: any) => `${i.endpoints ?? i.calls ?? 0} ${i.kind === "openapi" ? "routes" : "calls"} from ${i.source ?? "upload"}`)
        .join("; ");

      const msg = `Imported into Twin v${data.twinVersion}: ${importedKinds}`;
      setSummary(msg);
      toast.show(msg);
      onImported?.(data);
    } catch (err: any) {
      toast.show(err.message ?? "Failed to parse and upload specification.");
    } finally {
      setLoading(false);
      if (fileInput.current) fileInput.current.value = "";
    }
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    if (e.dataTransfer.files?.[0]) {
      uploadFile(e.dataTransfer.files[0]);
    }
  };

  return (
    <div
      onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
      onDragLeave={() => setDragOver(false)}
      onDrop={handleDrop}
      className={`relative flex flex-col items-center justify-center rounded-lg border-2 border-dashed p-4 text-center transition-colors ${
        dragOver ? "border-blue-500 bg-blue-50/50" : "border-slate-200 bg-slate-50/60 hover:bg-slate-50"
      }`}
    >
      <input
        ref={fileInput}
        type="file"
        accept=".json,.har"
        aria-label="Upload OpenAPI or HAR file"
        className="hidden"
        onChange={(e) => { if (e.target.files?.[0]) uploadFile(e.target.files[0]); }}
      />

      <div className="flex flex-wrap items-center justify-center gap-2">
        <span className="text-xs font-medium text-slate-700">Seed Twin from OpenAPI / Swagger / HAR:</span>
        <Button
          variant="secondary"
          size="sm"
          disabled={loading}
          onClick={() => fileInput.current?.click()}
        >
          {loading ? "Parsing & Seeding…" : "Choose .json / .har"}
        </Button>
        <span className="text-xs text-slate-400">or drop file here</span>
      </div>

      {summary && (
        <div className="mt-2.5 flex items-center gap-2 text-xs text-emerald-800">
          <Badge tone="success">Seeded</Badge>
          <span>{summary}</span>
        </div>
      )}
      {toast.node}
    </div>
  );
}
