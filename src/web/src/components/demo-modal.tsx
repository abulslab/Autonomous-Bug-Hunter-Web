"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import clsx from "clsx";
import { Badge, Button, Checkbox, Spinner } from "@/components/ui";
import { api, useAction, useApi } from "@/lib/api";

interface DemoModalProps {
  open: boolean;
  onClose: () => void;
}

export function DemoModal({ open, onClose }: DemoModalProps) {
  const router = useRouter();
  const status = useApi<any>("/hunter/status", { refetch: 5000 });
  const [activeApp, setActiveApp] = useState<string>("shop");
  const [defectState, setDefectState] = useState<Record<string, Record<string, boolean>>>({});
  const [savedMsg, setSavedMsg] = useState<string | null>(null);

  const toggle = useAction(
    async ({ app, flags }: { app: string; flags: Record<string, boolean> }) => {
      const res = await api<any>(`/hunter/demo/defects?app=${app}`, { method: "PUT", json: flags });
      return res;
    },
    ["/hunter/status"]
  );

  const demos: any[] = (status.data?.demos ?? []).filter((d: any) => d.enabled);

  // Initialize defect state from status when opened
  useEffect(() => {
    if (status.data?.demos) {
      const state: Record<string, Record<string, boolean>> = {};
      for (const d of status.data.demos) {
        if (d.defects) {
          state[d.app] = {};
          for (const def of d.defects) {
            state[d.app][def.id] = def.enabled;
          }
        }
      }
      setDefectState(state);
    }
  }, [status.data?.demos]);

  // Handle escape key
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape" && open) onClose();
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [open, onClose]);

  if (!open) return null;

  const currentDemo = demos.find((d) => d.app === activeApp) || demos[0];
  const currentDefects = currentDemo?.defects ?? [];
  const currentFlags = defectState[activeApp] || {};

  const handleToggleDefect = (id: string, value: boolean) => {
    setDefectState((prev) => ({
      ...prev,
      [activeApp]: {
        ...(prev[activeApp] || {}),
        [id]: value,
      },
    }));
  };

  const handleSetAll = (value: boolean) => {
    const updated: Record<string, boolean> = {};
    for (const d of currentDefects) {
      updated[d.id] = value;
    }
    setDefectState((prev) => ({
      ...prev,
      [activeApp]: updated,
    }));
  };

  const handleSave = async () => {
    if (!currentDemo) return;
    try {
      await toggle.mutateAsync({ app: activeApp, flags: currentFlags });
      setSavedMsg(`Saved defect configuration for ${currentDemo.name}`);
      setTimeout(() => setSavedMsg(null), 3000);
    } catch {
      setSavedMsg("Failed to save defect configuration.");
      setTimeout(() => setSavedMsg(null), 3000);
    }
  };

  const handleSelectPreset = (app: string) => {
    onClose();
    router.push(`/hunts/new?preset=${app}`);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/50 backdrop-blur-xs animate-in fade-in duration-200">
      <div
        className="w-full max-w-2xl rounded-2xl border border-slate-200 bg-white shadow-2xl overflow-hidden flex flex-col max-h-[90vh]"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 bg-slate-50/70">
          <div>
            <div className="flex items-center gap-2">
              <span className="text-base font-bold text-slate-900">Demo Targets & Seeded Defects</span>
              <Badge tone="accent">Sandbox</Badge>
            </div>
            <p className="text-xs text-slate-500 mt-0.5">
              Select a pre-configured target or adjust ground-truth defect injection to measure false positives.
            </p>
          </div>
          <button
            onClick={onClose}
            className="rounded-lg p-1.5 text-slate-400 hover:bg-slate-200 hover:text-slate-700 transition-colors"
          >
            <svg xmlns="http://www.w3.org/2000/svg" className="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
              <path
                fillRule="evenodd"
                d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                clipRule="evenodd"
              />
            </svg>
          </button>
        </div>

        {/* Body */}
        <div className="p-6 overflow-y-auto space-y-6">
          {/* Quick Demo Presets */}
          <div>
            <h3 className="text-xs font-bold uppercase tracking-wider text-slate-500 mb-3">
              Quick Demo Presets
            </h3>
            <div className="grid gap-3 sm:grid-cols-2">
              <div className="rounded-xl border border-slate-200 p-4 hover:border-blue-400 transition-colors bg-white">
                <div className="flex items-start justify-between">
                  <div>
                    <h4 className="text-sm font-bold text-slate-900">Demo Shop</h4>
                    <p className="font-mono text-xs text-slate-500 mt-0.5">http://localhost:5290/</p>
                  </div>
                  <Badge tone="success">Storefront</Badge>
                </div>
                <p className="text-xs text-slate-600 mt-2">
                  E-commerce store with catalog, coupons, search, cart, and checkout workflows.
                </p>
                <div className="mt-3 pt-3 border-t border-slate-100 flex items-center justify-between">
                  <span className="text-[11px] text-slate-400">8 Backlog Stories</span>
                  <Button
                    variant="primary"
                    size="sm"
                    onClick={() => handleSelectPreset("shop")}
                  >
                    Load into Hunt →
                  </Button>
                </div>
              </div>

              <div className="rounded-xl border border-slate-200 p-4 hover:border-blue-400 transition-colors bg-white">
                <div className="flex items-start justify-between">
                  <div>
                    <h4 className="text-sm font-bold text-slate-900">Demo Hotel</h4>
                    <p className="font-mono text-xs text-slate-500 mt-0.5">http://localhost:5291/</p>
                  </div>
                  <Badge tone="accent">Hospitality</Badge>
                </div>
                <p className="text-xs text-slate-600 mt-2">
                  Hotel booking system with date selectors, room capacities, breakfast, and cancellation.
                </p>
                <div className="mt-3 pt-3 border-t border-slate-100 flex items-center justify-between">
                  <span className="text-[11px] text-slate-400">6 Backlog Stories</span>
                  <Button
                    variant="primary"
                    size="sm"
                    onClick={() => handleSelectPreset("hotel")}
                  >
                    Load into Hunt →
                  </Button>
                </div>
              </div>
            </div>
          </div>

          {/* Configure Seeded Demo Defects */}
          <div>
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-xs font-bold uppercase tracking-wider text-slate-500">
                Configure Seeded Demo Defects
              </h3>
              <div className="flex gap-1.5">
                {demos.map((d) => (
                  <button
                    key={d.app}
                    type="button"
                    onClick={() => setActiveApp(d.app)}
                    className={clsx(
                      "px-2.5 py-1 text-xs font-medium rounded-md transition-colors",
                      activeApp === d.app
                        ? "bg-blue-600 text-white font-semibold"
                        : "bg-slate-100 text-slate-600 hover:bg-slate-200"
                    )}
                  >
                    {d.name}
                  </button>
                ))}
              </div>
            </div>

            {status.isLoading ? (
              <Spinner />
            ) : currentDefects.length === 0 ? (
              <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 text-center text-xs text-slate-500">
                No defect injection endpoints detected. Start the demo services to configure defects.
              </div>
            ) : (
              <div className="rounded-xl border border-slate-200 bg-slate-50/50 p-4">
                <div className="flex items-center justify-between mb-3 pb-2 border-b border-slate-200">
                  <span className="text-xs font-medium text-slate-700">
                    Defects for {currentDemo?.name} ({Object.values(currentFlags).filter(Boolean).length} of {currentDefects.length} enabled)
                  </span>
                  <div className="flex gap-2">
                    <Button variant="secondary" size="sm" onClick={() => handleSetAll(true)}>
                      All On
                    </Button>
                    <Button variant="secondary" size="sm" onClick={() => handleSetAll(false)}>
                      All Off (False-Positive Test)
                    </Button>
                  </div>
                </div>

                <div className="space-y-2.5 max-h-56 overflow-y-auto pr-1">
                  {currentDefects.map((def: any) => (
                    <div
                      key={def.id}
                      className="rounded-lg border border-slate-200 bg-white p-2.5 hover:border-slate-300 transition-colors"
                    >
                      <Checkbox
                        checked={currentFlags[def.id] ?? def.enabled}
                        onChange={(val) => handleToggleDefect(def.id, val)}
                        label={def.title}
                        hint={def.description}
                      />
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between border-t border-slate-200 px-6 py-3.5 bg-slate-50/70">
          <div>
            {savedMsg && (
              <span className="text-xs font-medium text-emerald-700 bg-emerald-50 px-2.5 py-1 rounded-md border border-emerald-200">
                ✓ {savedMsg}
              </span>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Button variant="secondary" size="sm" onClick={onClose}>
              Close
            </Button>
            <Button
              variant="primary"
              size="sm"
              disabled={toggle.isPending}
              onClick={handleSave}
            >
              {toggle.isPending ? "Saving…" : "Save Defect Configuration"}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
