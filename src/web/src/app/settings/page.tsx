"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useEffect, useState } from "react";
import clsx from "clsx";
import { Badge, Button, ErrorBox, Field, Input, PageHeader, Panel, Select, Spinner, Table } from "@/components/ui";
import { API, api, useAction, useApi } from "@/lib/api";
import { loadUserSettings, saveUserSettings, UserSettings } from "@/lib/settings";

type SettingsSection = "llm" | "services" | "security";

export default function SettingsPage() {
  const status = useApi<any>("/hunter/status", { refetch: 10000 });
  const [section, setSection] = useState<SettingsSection>("llm");
  const [checks, setChecks] = useState<Record<string, any>>({});
  const [custom, setCustom] = useState("");
  
  // Model settings configuration
  const [driverModel, setDriverModel] = useState<string>("");
  const [plannerModel, setPlannerModel] = useState<string>("");
  const [budget, setBudget] = useState<number>(1);
  const [maxSteps, setMaxSteps] = useState<number>(60);
  const [savedNotice, setSavedNotice] = useState<string | null>(null);

  // Runtime BYOK API key configuration (stored in memory on the API server; never persisted to git)
  const [geminiKey, setGeminiKey] = useState("");
  const [anthropicKey, setAnthropicKey] = useState("");
  const [openAiKey, setOpenAiKey] = useState("");
  const [ollamaUrl, setOllamaUrl] = useState("http://localhost:11434");
  const [keyNotice, setKeyNotice] = useState<string | null>(null);

  const saveProviderKeys = useAction(
    async () => {
      const payload: Record<string, string> = {};
      if (geminiKey.trim()) payload.geminiApiKey = geminiKey.trim();
      if (anthropicKey.trim()) payload.anthropicApiKey = anthropicKey.trim();
      if (openAiKey.trim()) payload.openAiApiKey = openAiKey.trim();
      if (ollamaUrl.trim()) payload.ollamaUrl = ollamaUrl.trim();
      return api<any>("/hunter/models/config", { method: "POST", json: payload });
    },
    ["/hunter/status"]
  );

  const s = status.data;

  useEffect(() => {
    const saved = loadUserSettings();
    if (saved.driverModel) {
      setDriverModel(saved.driverModel);
    } else if (s?.defaults?.driver) {
      setDriverModel(s.defaults.driver);
    }

    if (saved.plannerModel) {
      setPlannerModel(saved.plannerModel);
    } else if (s?.defaults?.planner) {
      setPlannerModel(s.defaults.planner);
    }

    if (saved.maxCostUsd != null) {
      setBudget(saved.maxCostUsd);
    } else if (s?.defaults?.maxCostUsd != null) {
      setBudget(s.defaults.maxCostUsd);
    }

    if (saved.maxSteps != null) {
      setMaxSteps(saved.maxSteps);
    }
    if (s?.providers?.ollamaUrl) {
      setOllamaUrl(s.providers.ollamaUrl);
    }
  }, [s?.defaults, s?.providers?.ollamaUrl]);

  if (status.error) return <ErrorBox error={status.error} />;
  if (!status.data) return <Spinner />;

  const check = async (id: string) => {
    setChecks((c) => ({ ...c, [id]: { pending: true } }));
    const r = await api<any>("/hunter/models/check", { method: "POST", json: { connection: id } }).catch((e) => ({ ok: false, error: e.message }));
    setChecks((c) => ({ ...c, [id]: r }));
  };

  const models: { id: string; label: string }[] = [
    ...(s.models ?? []),
    ...((s.ollama?.models ?? []) as string[]).map((m) => ({ id: `ollama:${m}`, label: `Ollama (local) ${m}` })),
  ];

  const handleSaveModelConfig = () => {
    const newSettings: UserSettings = {
      driverModel,
      plannerModel: plannerModel === "same" ? "" : plannerModel,
      maxCostUsd: budget,
      maxSteps,
    };
    saveUserSettings(newSettings);
    setSavedNotice("Model setup saved. New hunts will automatically use these defaults.");
    setTimeout(() => setSavedNotice(null), 3500);
  };

  const row = (id: string, label: string) => {
    const c = checks[id];
    const isDefaultDriver = id === (driverModel || s.defaults?.driver);
    const isDefaultPlanner = id === (plannerModel || s.defaults?.planner);

    return [
      <div key="m">
        <div className="font-medium text-slate-800">{label}</div>
        <div className="font-mono text-xs text-slate-500">{id}</div>
      </div>,
      <div key="r" className="flex flex-wrap gap-1">
        {isDefaultDriver && <Badge tone="accent">Default Driver</Badge>}
        {isDefaultPlanner && <Badge tone="neutral">Default Planner</Badge>}
      </div>,
      c?.pending ? <span key="c" className="text-xs text-slate-500">Checking…</span> : c ? (
        <span key="c" className="text-xs">
          {c.ok ? <Badge tone="success">Connected · {c.ms} ms</Badge> : <><Badge tone="danger">Failed</Badge> <span className="text-slate-600 ml-1">{c.error}</span></>}
        </span>
      ) : "",
      <Button key="b" variant="secondary" size="sm" onClick={() => check(id)}>Test connection</Button>,
    ];
  };

  return (
    <>
      <PageHeader
        title="Settings & System Configuration"
        subtitle="Manage default AI models, provider integrations, browser execution sandboxes, and security boundaries."
      />

      <div className="flex flex-col md:flex-row gap-6 items-start">
        {/* Settings Left Navigation */}
        <div className="w-full md:w-56 shrink-0 rounded-xl border border-slate-200 bg-white p-3 shadow-xs">
          <div className="mb-2 px-2 text-[11px] font-semibold uppercase tracking-wider text-slate-400">
            Settings Menu
          </div>
          <nav className="space-y-1">
            <button
              type="button"
              onClick={() => setSection("llm")}
              className={clsx(
                "w-full flex items-center justify-between rounded-lg px-3 py-2 text-sm font-medium transition-colors text-left",
                section === "llm" ? "bg-blue-50 text-blue-700 font-semibold" : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              )}
            >
              <span>LLM Integration</span>
              <span className="text-[10px] rounded-full bg-slate-100 px-1.5 py-0.5 text-slate-500">{models.length}</span>
            </button>
            <button
              type="button"
              onClick={() => setSection("services")}
              className={clsx(
                "w-full flex items-center rounded-lg px-3 py-2 text-sm font-medium transition-colors text-left",
                section === "services" ? "bg-blue-50 text-blue-700 font-semibold" : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              )}
            >
              Services & Workers
            </button>
            <button
              type="button"
              onClick={() => setSection("security")}
              className={clsx(
                "w-full flex items-center rounded-lg px-3 py-2 text-sm font-medium transition-colors text-left",
                section === "security" ? "bg-blue-50 text-blue-700 font-semibold" : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              )}
            >
              Security & Secrets
            </button>
          </nav>
        </div>

        {/* Settings Content Area */}
        <div className="min-w-0 flex-1 w-full space-y-6">
          {section === "llm" && (
            <>
              {/* One-Time Model Configuration Section (User Requirement 4) */}
              <Panel
                title="AI Model Selection (One-time Setup)"
                subtitle="Select the default foundation models used for autonomous browser exploration and intent synthesis."
              >
                <div className="space-y-4">
                  <div className="grid gap-4 sm:grid-cols-2">
                    <Field label="Default AI Driver Model" hint="Selects browser actions, forms, and clicks">
                      <Select value={driverModel} onChange={(e) => setDriverModel(e.target.value)}>
                        {models.map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.label} ({m.id})
                          </option>
                        ))}
                      </Select>
                    </Field>

                    <Field label="Default Intent Planner Model" hint="Synthesizes requirements & invariants">
                      <Select value={plannerModel || "same"} onChange={(e) => setPlannerModel(e.target.value)}>
                        <option value="same">Same as Driver Model</option>
                        {models.map((m) => (
                          <option key={m.id} value={m.id}>
                            {m.label} ({m.id})
                          </option>
                        ))}
                      </Select>
                    </Field>
                  </div>

                  <div className="grid gap-4 sm:grid-cols-2 pt-2 border-t border-slate-100">
                    <Field label="Default AI Budget Limit ($ USD)" hint="Stops AI calls when budget reached">
                      <Input
                        type="number"
                        min={0}
                        max={50}
                        step={0.25}
                        value={budget}
                        onChange={(e) => setBudget(Number(e.target.value))}
                      />
                    </Field>

                    <Field label="Default Step Budget" hint="Max exploration actions per hunt">
                      <Input
                        type="number"
                        min={10}
                        max={150}
                        value={maxSteps}
                        onChange={(e) => setMaxSteps(Number(e.target.value))}
                      />
                    </Field>
                  </div>

                  <div className="flex items-center justify-between pt-2">
                    <div>
                      {savedNotice && (
                        <span className="text-xs font-semibold text-emerald-700 bg-emerald-50 px-3 py-1.5 rounded-md border border-emerald-200">
                          ✓ {savedNotice}
                        </span>
                      )}
                    </div>
                    <Button variant="primary" size="sm" onClick={handleSaveModelConfig}>
                      Save Model Setup
                    </Button>
                  </div>
                </div>
              </Panel>

              {/* Provider API Keys (Bring Your Own Key) */}
              <Panel
                title="Provider API Keys (Bring Your Own Key)"
                subtitle="Configure your own API keys for this instance, or set GEMINI_API_KEY / ANTHROPIC_API_KEY / OPENAI_API_KEY in your environment. Keys are held in server memory only and never committed to git."
              >
                <div className="space-y-4">
                  <div className="grid gap-4 sm:grid-cols-2">
                    <Field
                      label="Google Gemini API Key"
                      hint={s.providers?.gemini ? "Configured (Active)" : "From Google AI Studio (aistudio.google.com), or Google application default credentials"}
                    >
                      <Input
                        type="password"
                        value={geminiKey}
                        onChange={(e) => setGeminiKey(e.target.value)}
                        placeholder={s.providers?.gemini ? "•••••••••••••••• (configured)" : "Paste GEMINI_API_KEY"}
                      />
                    </Field>

                    <Field
                      label="Anthropic Claude API Key"
                      hint={s.providers?.anthropic ? "Configured (Active)" : "Optional"}
                    >
                      <Input
                        type="password"
                        value={anthropicKey}
                        onChange={(e) => setAnthropicKey(e.target.value)}
                        placeholder={s.providers?.anthropic ? "•••••••••••••••• (configured)" : "Paste ANTHROPIC_API_KEY"}
                      />
                    </Field>

                    <Field
                      label="OpenAI API Key"
                      hint={s.providers?.openai ? "Configured (Active)" : "Optional"}
                    >
                      <Input
                        type="password"
                        value={openAiKey}
                        onChange={(e) => setOpenAiKey(e.target.value)}
                        placeholder={s.providers?.openai ? "•••••••••••••••• (configured)" : "Paste OPENAI_API_KEY"}
                      />
                    </Field>

                    <Field label="Local Ollama Endpoint" hint={s.ollama?.available ? `Connected (${s.ollama.models.length} local models)` : "No API key required"}>
                      <Input
                        value={ollamaUrl}
                        onChange={(e) => setOllamaUrl(e.target.value)}
                        placeholder="http://localhost:11434"
                      />
                    </Field>
                  </div>

                  <div className="flex items-center justify-between pt-2">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge tone={s.providers?.gemini ? "success" : "muted"}>Gemini: {s.providers?.gemini ? "Ready" : "Unset"}</Badge>
                      <Badge tone={s.providers?.anthropic ? "success" : "muted"}>Anthropic: {s.providers?.anthropic ? "Ready" : "Unset"}</Badge>
                      <Badge tone={s.providers?.openai ? "success" : "muted"}>OpenAI: {s.providers?.openai ? "Ready" : "Unset"}</Badge>
                      <Badge tone={s.ollama?.available ? "success" : "muted"}>Ollama: {s.ollama?.available ? "Ready" : "Offline"}</Badge>
                      {keyNotice && (
                        <span className="text-xs font-semibold text-emerald-700 bg-emerald-50 px-2.5 py-1 rounded-md border border-emerald-200">
                          ✓ {keyNotice}
                        </span>
                      )}
                    </div>
                    <Button
                      variant="secondary"
                      size="sm"
                      disabled={saveProviderKeys.isPending}
                      onClick={() =>
                        saveProviderKeys.mutate(undefined as never, {
                          onSuccess: () => {
                            setGeminiKey("");
                            setAnthropicKey("");
                            setOpenAiKey("");
                            setKeyNotice("Provider keys updated in server memory.");
                            setTimeout(() => setKeyNotice(null), 3500);
                          },
                        })
                      }
                    >
                      {saveProviderKeys.isPending ? "Applying…" : "Apply Provider Keys"}
                    </Button>
                  </div>
                </div>
              </Panel>

              {/* Provider Connections & Verification */}
              <Panel
                title="Configured Providers & Latency Verification"
                subtitle="Live status of configured API keys and local Ollama instances. Test JSON output and latency directly."
                flush
              >
                <Table
                  head={["Model", "Role", "Status & Latency", "Action"]}
                  empty="No models configured. Set GEMINI_API_KEY, ANTHROPIC_API_KEY, or run local Ollama."
                  rows={models.map((m) => row(m.id, m.label))}
                />
                <div className="flex flex-wrap items-center gap-2 border-t border-slate-200 bg-slate-50/50 px-4 py-3">
                  <span className="text-xs font-medium text-slate-700">Test Custom Connection:</span>
                  <Input
                    className="max-w-sm font-mono text-xs bg-white"
                    value={custom}
                    onChange={(e) => setCustom(e.target.value)}
                    placeholder="provider:model (e.g. gemini:gemini-2.5-flash)"
                  />
                  <Button variant="secondary" size="sm" disabled={!custom.trim()} onClick={() => check(custom.trim())}>
                    Verify
                  </Button>
                  {checks[custom.trim()] && !checks[custom.trim()].pending && (
                    <span className="text-xs">
                      {checks[custom.trim()].ok ? (
                        <Badge tone="success">Verified · {checks[custom.trim()].ms} ms</Badge>
                      ) : (
                        <span className="text-red-700">{checks[custom.trim()].error}</span>
                      )}
                    </span>
                  )}
                </div>
              </Panel>
            </>
          )}

          {section === "services" && (
            <div className="grid gap-6">
              <Panel title="Platform Services & Daemons">
                <dl className="grid grid-cols-[160px_1fr] gap-y-3 text-sm">
                  <dt className="text-slate-500">Bug Hunter API</dt>
                  <dd className="font-mono text-xs text-slate-800">
                    {API} ·{" "}
                    <a className="text-blue-700 hover:underline" href={`${API}/docs`} target="_blank" rel="noreferrer">
                      OpenAPI Interactive Docs
                    </a>
                  </dd>
                  <dt className="text-slate-500">Browser Worker</dt>
                  <dd className="flex items-center gap-2">
                    {s.worker.ok ? <Badge tone="success">Ready</Badge> : <Badge tone="warning">{s.worker.autoStart ? "Starting" : "Offline"}</Badge>}
                    <span className="font-mono text-xs text-slate-500">{s.worker.url}</span>
                  </dd>
                  <dt className="text-slate-500">Demo Storefront</dt>
                  <dd className="flex items-center gap-2">
                    {s.storefront.enabled ? <Badge tone="success">Running</Badge> : <Badge tone="muted">Not running</Badge>}
                    <span className="font-mono text-xs text-slate-500">{s.storefront.url}</span>
                  </dd>
                  <dt className="text-slate-500">Ollama Local AI</dt>
                  <dd className="flex items-center gap-2">
                    {s.ollama?.available ? (
                      <Badge tone="success">{s.ollama.models.length} models detected</Badge>
                    ) : (
                      <Badge tone="muted">Offline / Not detected</Badge>
                    )}
                  </dd>
                </dl>
              </Panel>
            </div>
          )}

          {section === "security" && (
            <div className="space-y-6">
              <Panel title="Security Architecture & Secret Substitution">
                <div className="space-y-3 text-sm text-slate-700 leading-relaxed">
                  <p>
                    Autonomous Bug Hunter handles secrets this way:
                  </p>
                  <ul className="list-disc space-y-1 pl-5 text-xs text-slate-600">
                    <li><strong>Secret Substitution:</strong> AI models never receive raw passwords or API keys. Secrets are replaced with symbolic placeholders (e.g. <code>{"{{secret:password}}"}</code>) and only decrypted in the isolated browser worker.</li>
                    <li><strong>Host Allowlisting:</strong> Browser sessions are locked to the target application host. Outbound SSRF attacks to internal metadata services (e.g. AWS 169.254.169.254) are rejected at the network proxy layer.</li>
                    <li><strong>Dynamic Account Partitioning:</strong> RFC 5233 sub-addressing isolates user sessions without destructive database wipes.</li>
                  </ul>
                </div>
              </Panel>

              <Panel title="Configured Environment Variables Reference">
                <dl className="grid grid-cols-[200px_1fr] gap-y-2 text-xs font-mono">
                  <dt className="text-slate-500">GEMINI_API_KEY</dt>
                  <dd className="text-slate-800">Google AI Studio API key</dd>
                  <dt className="text-slate-500">ANTHROPIC_API_KEY</dt>
                  <dd className="text-slate-800">Anthropic API key</dd>
                  <dt className="text-slate-500">OPENAI_API_KEY</dt>
                  <dd className="text-slate-800">OpenAI-compatible API key</dd>
                  <dt className="text-slate-500">BUGHUNTER_API_KEY</dt>
                  <dd className="text-slate-800">Optional API Gateway bearer authentication</dd>
                </dl>
              </Panel>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
