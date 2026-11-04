"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useMemo, useState } from "react";
import { Badge, Button, Code, Panel, statusTone } from "@/components/ui";
import { Shot, ShotViewer, describe, driverLabel } from "@/components/hunt";

interface ReplayInspectorProps {
  hunt: any;
  initialStep?: number;
}

export function ReplayInspector({ hunt, initialStep }: ReplayInspectorProps) {
  const steps = useMemo(() => hunt.steps?.filter((s: any) => s.n > 0) || [], [hunt.steps]);
  const findings = useMemo(() => hunt.findings || [], [hunt.findings]);

  const [activeStep, setActiveStep] = useState<number>(() => {
    if (initialStep != null && steps.some((s: any) => s.n === initialStep)) return initialStep;
    const firstFindingStep = findings.find((f: any) => f.reproduced === "Confirmed")?.step;
    return firstFindingStep ?? steps[0]?.n ?? 0;
  });

  const [zoom, setZoom] = useState<number | null>(null);
  const [onlyMinimal, setOnlyMinimal] = useState(false);

  // Collect all minimal steps from confirmed findings
  const minimalStepNumbers = useMemo(() => {
    const set = new Set<number>();
    for (const f of findings) {
      if (f.minimalSteps) {
        for (const n of f.minimalSteps) set.add(n);
      }
    }
    return set;
  }, [findings]);

  const displayedSteps = useMemo(() => {
    if (!onlyMinimal || minimalStepNumbers.size === 0) return steps;
    return steps.filter((s: any) => minimalStepNumbers.has(s.n));
  }, [steps, onlyMinimal, minimalStepNumbers]);

  const currentIndex = displayedSteps.findIndex((s: any) => s.n === activeStep);
  const currentStep = steps.find((s: any) => s.n === activeStep) ?? displayedSteps[0];

  const goPrev = () => {
    if (currentIndex > 0) setActiveStep(displayedSteps[currentIndex - 1].n);
  };

  const goNext = () => {
    if (currentIndex >= 0 && currentIndex < displayedSteps.length - 1) {
      setActiveStep(displayedSteps[currentIndex + 1].n);
    }
  };

  const currentFindings = useMemo(() => {
    if (!currentStep) return [];
    return findings.filter((f: any) => f.step === currentStep.n || currentStep.findingIds?.includes(f.id));
  }, [findings, currentStep]);

  if (!steps.length) {
    return <div className="p-8 text-center text-sm text-slate-500">No recorded steps in this hunt.</div>;
  }

  return (
    <div className="space-y-4">
      {/* Control Bar: Scrubber, Navigation, and Quick-Jump Badges */}
      <div className="rounded-lg border border-slate-200 bg-white p-3.5 shadow-xs">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <span className="text-xs font-semibold uppercase tracking-wider text-slate-500">Step Scrubber</span>
            <Badge tone="accent">#{currentStep?.n ?? 0} of {steps[steps.length - 1]?.n ?? steps.length}</Badge>
            {minimalStepNumbers.has(currentStep?.n) && (
              <Badge tone="success" title="This step is part of the delta-debugged minimal reproducing sequence">
                Minimal Repro Path
              </Badge>
            )}
            {minimalStepNumbers.size > 0 && (
              <label className="ml-2 flex cursor-pointer items-center gap-1.5 text-xs text-slate-600">
                <input
                  type="checkbox"
                  checked={onlyMinimal}
                  onChange={(e) => {
                    setOnlyMinimal(e.target.checked);
                    if (e.target.checked && !minimalStepNumbers.has(activeStep)) {
                      const firstMin = [...minimalStepNumbers][0];
                      if (firstMin != null) setActiveStep(firstMin);
                    }
                  }}
                  className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                />
                Filter to minimal repro ({minimalStepNumbers.size} steps)
              </label>
            )}
          </div>

          <div className="flex items-center gap-1.5">
            <Button variant="secondary" size="sm" disabled={currentIndex <= 0} onClick={() => setActiveStep(displayedSteps[0].n)}>
              First
            </Button>
            <Button variant="secondary" size="sm" disabled={currentIndex <= 0} onClick={goPrev}>
              Previous
            </Button>
            <Button variant="secondary" size="sm" disabled={currentIndex >= displayedSteps.length - 1} onClick={goNext}>
              Next
            </Button>
            <Button variant="secondary" size="sm" disabled={currentIndex >= displayedSteps.length - 1} onClick={() => setActiveStep(displayedSteps[displayedSteps.length - 1].n)}>
              Last
            </Button>
          </div>
        </div>

        {/* Step Slider */}
        <div className="mt-3 flex items-center gap-3">
          <input
            type="range"
            min={0}
            max={displayedSteps.length - 1}
            value={Math.max(0, currentIndex)}
            onChange={(e) => setActiveStep(displayedSteps[Number(e.target.value)].n)}
            className="h-1.5 w-full cursor-pointer appearance-none rounded-lg bg-slate-200 accent-blue-600"
          />
        </div>

        {/* Quick jump to confirmed finding steps */}
        {findings.length > 0 && (
          <div className="mt-2.5 flex flex-wrap items-center gap-1.5 pt-2 border-t border-slate-100 text-xs">
            <span className="text-slate-500">Jump to Finding:</span>
            {findings.map((f: any) => (
              <button
                key={f.id}
                type="button"
                onClick={() => setActiveStep(f.step)}
                className={`inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-[11px] font-medium transition-colors ${
                  activeStep === f.step
                    ? "bg-red-700 text-white"
                    : f.reproduced === "Confirmed"
                    ? "bg-red-50 text-red-700 hover:bg-red-100 border border-red-200"
                    : "bg-slate-100 text-slate-700 hover:bg-slate-200"
                }`}
              >
                <span>{f.id}</span>
                <span className="truncate max-w-[150px]">({f.title})</span>
                <span className="opacity-70">@ #{f.step}</span>
              </button>
            ))}
          </div>
        )}
      </div>

      {/* Synchronized Split-Pane Grid */}
      <div className="grid gap-5 lg:grid-cols-[1.1fr_1fr]">
        {/* Left Pane: Visual Snapshot & DOM Target Surface */}
        <div className="space-y-4">
          <Panel
            title={<span className="flex items-center gap-2"><span>Visual State: Step #{currentStep?.n}</span><Badge tone={currentStep?.ok ? "success" : "danger"}>{currentStep?.ok ? "Success" : "Error"}</Badge></span>}
            subtitle={currentStep?.url}
            actions={<Button variant="secondary" size="sm" onClick={() => setZoom(currentStep?.n)}>Full Screen</Button>}
          >
            <Shot hunt={hunt.id} n={currentStep?.n} onOpen={() => setZoom(currentStep?.n)} />

            {/* DOM Element Surface */}
            {currentStep?.target && (
              <div className="mt-3 rounded-md border border-slate-200 bg-slate-50 p-3 text-xs">
                <div className="font-semibold text-slate-700">Interacted DOM Element</div>
                <dl className="mt-1.5 grid grid-cols-[80px_1fr] gap-x-2 gap-y-1">
                  <dt className="text-slate-500">Element</dt>
                  <dd className="font-mono text-slate-800">&lt;{currentStep.target.tag || "element"}&gt; {currentStep.target.role ? `role="${currentStep.target.role}"` : ""}</dd>
                  {currentStep.target.name && (<><dt className="text-slate-500">Name</dt><dd className="text-slate-800">&quot;{currentStep.target.name}&quot;</dd></>)}
                  {currentStep.target.label && (<><dt className="text-slate-500">Label</dt><dd className="text-slate-800">{currentStep.target.label}</dd></>)}
                  {currentStep.target.value != null && (<><dt className="text-slate-500">Value</dt><dd className="font-mono text-slate-800">{String(currentStep.target.value)}</dd></>)}
                  {currentStep.target.testid && (<><dt className="text-slate-500">Test ID</dt><dd className="font-mono text-slate-800">{currentStep.target.testid}</dd></>)}
                  {currentStep.target.invalid && (<><dt className="text-slate-500">Validation</dt><dd className="text-red-700">{currentStep.target.invalid}</dd></>)}
                </dl>
              </div>
            )}
          </Panel>
        </div>

        {/* Right Pane: Action, HTTP Exchange & Invariant Evaluation */}
        <div className="space-y-4">
          {/* Action Details */}
          <Panel
            title={<span className="flex items-center gap-2"><span>Action Details</span><Badge tone="muted">{driverLabel[currentStep?.driver] ?? currentStep?.driver ?? "Action"}</Badge></span>}
          >
            <div className="space-y-2 text-xs">
              <div className="rounded-md border border-slate-100 bg-slate-50 p-2.5 font-medium text-slate-800">
                {describe(currentStep?.action, currentStep?.target)}
              </div>
              {currentStep?.reasoning && (
                <div>
                  <span className="font-semibold text-slate-600">Model Reasoning: </span>
                  <span className="text-slate-700">{currentStep.reasoning}</span>
                </div>
              )}
              {currentStep?.error && (
                <div className="text-red-700">
                  <span className="font-semibold">Error: </span>{currentStep.error}
                </div>
              )}
              {currentStep?.blocked && (
                <div className="text-amber-800">
                  <span className="font-semibold">Blocked: </span>{currentStep.blocked}
                </div>
              )}
            </div>
          </Panel>

          {/* Recorded Network Traffic on this Step */}
          <Panel
            title={<span className="flex items-center gap-2"><span>Recorded HTTP Requests</span><Badge tone="accent">{currentStep?.requests?.length ?? 0}</Badge></span>}
            subtitle="Network calls triggered by this action in the browser worker"
          >
            {currentStep?.requests?.length ? (
              <ul className="divide-y divide-slate-100 font-mono text-xs text-slate-800">
                {currentStep.requests.map((req: string, idx: number) => {
                  const parts = req.split(" ");
                  const method = parts[0];
                  const rest = parts.slice(1).join(" ");
                  return (
                    <li key={idx} className="flex items-center gap-2 py-2">
                      <span className={`inline-block rounded px-1.5 py-0.5 text-[10px] font-bold ${
                        method === "GET" ? "bg-blue-100 text-blue-800" :
                        method === "POST" ? "bg-emerald-100 text-emerald-800" :
                        method === "DELETE" ? "bg-red-100 text-red-800" : "bg-purple-100 text-purple-800"
                      }`}>
                        {method}
                      </span>
                      <span className="truncate flex-1">{rest}</span>
                    </li>
                  );
                })}
              </ul>
            ) : (
              <p className="text-xs text-slate-500">No external network requests were dispatched during this step.</p>
            )}
          </Panel>

          {/* Triggered Findings & Invariant Evaluations */}
          {currentFindings.length > 0 && (
            <Panel
              title={<span className="flex items-center gap-2"><span>Findings Triggered on Step</span><Badge tone="danger">{currentFindings.length}</Badge></span>}
            >
              <div className="space-y-3">
                {currentFindings.map((f: any) => (
                  <div key={f.id} className="rounded-md border border-red-200 bg-red-50/50 p-3 text-xs">
                    <div className="flex items-center justify-between gap-2 border-b border-red-100 pb-1.5">
                      <span className="font-bold text-red-900">{f.title}</span>
                      <Badge tone={statusTone(f.reproduced)}>{f.reproduced ?? "Observed"}</Badge>
                    </div>
                    <dl className="mt-2 grid grid-cols-[70px_1fr] gap-x-2 gap-y-1">
                      <dt className="text-slate-500">Oracle</dt>
                      <dd className="font-mono text-slate-800">{f.oracle}</dd>
                      <dt className="text-slate-500">Expected</dt>
                      <dd className="text-slate-800">{f.expected}</dd>
                      <dt className="text-slate-500">Actual</dt>
                      <dd className="text-red-700 font-semibold">{f.actual}</dd>
                    </dl>
                    {f.evidence && (
                      <div className="mt-2">
                        <span className="font-medium text-slate-600">Recorded Evidence:</span>
                        <Code className="mt-1 max-h-36">{f.evidence}</Code>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </Panel>
          )}
        </div>
      </div>

      {zoom != null && (
        <ShotViewer
          hunt={hunt.id}
          steps={steps}
          n={zoom}
          onChange={setZoom}
          onClose={() => setZoom(null)}
        />
      )}
    </div>
  );
}
