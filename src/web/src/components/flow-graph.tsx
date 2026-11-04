"use client";
import React, { useMemo, useState } from "react";
import clsx from "clsx";

export interface GraphNode {
  id: string;
  title?: string;
  visits?: number;
  status?: string;
}

export interface GraphEdge {
  from: string;
  to: string;
  signature?: string;
  ok?: number;
  failed?: number;
}

export function StateFlowGraph({
  states,
  roads,
  onSelectNode,
  className,
}: {
  states: GraphNode[];
  roads: GraphEdge[];
  onSelectNode?: (nodeId: string | null) => void;
  className?: string;
}) {
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const handleSelect = (id: string | null) => {
    const next = selectedId === id ? null : id;
    setSelectedId(next);
    onSelectNode?.(next);
  };

  // Node layout calculation (Grid / Force-spaced circle layout in responsive SVG coordinate space)
  const layout = useMemo(() => {
    const width = 800;
    const height = 460;
    const padding = 60;
    const nodeWidth = 160;
    const nodeHeight = 54;

    const list = states.slice(0, 16); // focus on top states to keep graph legible
    const total = list.length;
    if (total === 0) return { nodes: [], edges: [], width, height };

    // Arrange in a layered grid or oval based on count
    const positions = new Map<string, { x: number; y: number }>();

    if (total <= 6) {
      // 2 columns or horizontal pipeline
      const cols = Math.min(3, total);
      const rows = Math.ceil(total / cols);
      const xStep = (width - padding * 2 - nodeWidth) / Math.max(1, cols - 1);
      const yStep = (height - padding * 2 - nodeHeight) / Math.max(1, rows - 1);

      list.forEach((s, i) => {
        const col = i % cols;
        const row = Math.floor(i / cols);
        positions.set(s.id, {
          x: padding + col * xStep,
          y: padding + row * yStep,
        });
      });
    } else {
      // Ellipse layout around center
      const cx = width / 2;
      const cy = height / 2;
      const rx = (width - padding * 2 - nodeWidth) / 2;
      const ry = (height - padding * 2 - nodeHeight) / 2;

      list.forEach((s, i) => {
        const angle = (2 * Math.PI * i) / total - Math.PI / 2;
        positions.set(s.id, {
          x: cx + rx * Math.cos(angle) - nodeWidth / 2,
          y: cy + ry * Math.sin(angle) - nodeHeight / 2,
        });
      });
    }

    const positionedNodes = list.map((s) => ({
      ...s,
      pos: positions.get(s.id) || { x: padding, y: padding },
      w: nodeWidth,
      h: nodeHeight,
    }));

    // Filter edges to only include nodes present in the layout
    const visibleIds = new Set(list.map((s) => s.id));
    const positionedEdges = roads
      .filter((r) => visibleIds.has(r.from) && visibleIds.has(r.to))
      .map((r, i) => {
        const src = positions.get(r.from)!;
        const dst = positions.get(r.to)!;
        return {
          id: `${r.from}-${r.to}-${i}`,
          ...r,
          srcCenter: { x: src.x + nodeWidth / 2, y: src.y + nodeHeight / 2 },
          dstCenter: { x: dst.x + nodeWidth / 2, y: dst.y + nodeHeight / 2 },
        };
      });

    return { nodes: positionedNodes, edges: positionedEdges, width, height };
  }, [states, roads]);

  if (layout.nodes.length === 0) {
    return (
      <div className="flex h-48 items-center justify-center rounded-lg border border-slate-200 bg-white text-xs text-slate-400">
        No state transitions mapped yet
      </div>
    );
  }

  const selectedNode = layout.nodes.find((n) => n.id === selectedId);

  return (
    <div className={clsx("rounded-lg border border-slate-200 bg-white p-4", className)}>
      <div className="mb-3 flex items-center justify-between">
        <div>
          <span className="text-xs font-semibold text-slate-800">State Transition Topology</span>
          <span className="ml-2 text-[11px] text-slate-500">
            {layout.nodes.length} states, {layout.edges.length} active routes
          </span>
        </div>
        {selectedId && (
          <div className="flex items-center gap-2">
            <span className="font-mono text-xs text-blue-700">Selected: {selectedId}</span>
            <button
              onClick={() => handleSelect(null)}
              className="text-xs text-slate-500 hover:text-slate-800"
            >
              Clear
            </button>
          </div>
        )}
      </div>

      <div className="relative overflow-hidden rounded-md border border-slate-100 bg-slate-50/50">
        <svg
          viewBox={`0 0 ${layout.width} ${layout.height}`}
          className="h-auto w-full select-none"
        >
          <defs>
            <marker
              id="arrow-default"
              viewBox="0 0 10 10"
              refX="8"
              refY="5"
              markerWidth="6"
              markerHeight="6"
              orient="auto-start-reverse"
            >
              <path d="M 0 1 L 9 5 L 0 9 z" fill="#94a3b8" />
            </marker>
            <marker
              id="arrow-active"
              viewBox="0 0 10 10"
              refX="8"
              refY="5"
              markerWidth="7"
              markerHeight="7"
              orient="auto-start-reverse"
            >
              <path d="M 0 1 L 9 5 L 0 9 z" fill="#1d4ed8" />
            </marker>
            <marker
              id="arrow-failed"
              viewBox="0 0 10 10"
              refX="8"
              refY="5"
              markerWidth="6"
              markerHeight="6"
              orient="auto-start-reverse"
            >
              <path d="M 0 1 L 9 5 L 0 9 z" fill="#ef4444" />
            </marker>
          </defs>

          {/* Edges */}
          {layout.edges.map((e) => {
            const isConnected = selectedId
              ? e.from === selectedId || e.to === selectedId
              : false;
            const isFaded = selectedId ? !isConnected : false;
            const hasFailed = (e.failed ?? 0) > 0;

            const dx = e.dstCenter.x - e.srcCenter.x;
            const dy = e.dstCenter.y - e.srcCenter.y;
            // Quadratic curvature offset
            const mx = (e.srcCenter.x + e.dstCenter.x) / 2 - dy * 0.12;
            const my = (e.srcCenter.y + e.dstCenter.y) / 2 + dx * 0.12;

            const markerId = isConnected
              ? "url(#arrow-active)"
              : hasFailed
              ? "url(#arrow-failed)"
              : "url(#arrow-default)";

            const strokeColor = isConnected
              ? "#1d4ed8"
              : hasFailed
              ? "#ef4444"
              : "#cbd5e1";

            return (
              <g key={e.id} className={clsx("transition-opacity", isFaded && "opacity-20")}>
                <path
                  d={`M ${e.srcCenter.x} ${e.srcCenter.y} Q ${mx} ${my} ${e.dstCenter.x} ${e.dstCenter.y}`}
                  fill="none"
                  stroke={strokeColor}
                  strokeWidth={isConnected ? 2.5 : 1.5}
                  markerEnd={markerId}
                />
              </g>
            );
          })}

          {/* Nodes */}
          {layout.nodes.map((n) => {
            const isSelected = selectedId === n.id;
            const isConnected = selectedId
              ? layout.edges.some(
                  (e) =>
                    (e.from === selectedId && e.to === n.id) ||
                    (e.to === selectedId && e.from === n.id)
                )
              : false;
            const isFaded = selectedId ? !isSelected && !isConnected : false;

            return (
              <g
                key={n.id}
                transform={`translate(${n.pos.x}, ${n.pos.y})`}
                onClick={() => handleSelect(n.id)}
                className={clsx(
                  "cursor-pointer transition-all",
                  isFaded && "opacity-30"
                )}
              >
                <rect
                  width={n.w}
                  height={n.h}
                  rx="6"
                  fill="#ffffff"
                  stroke={isSelected ? "#1d4ed8" : isConnected ? "#60a5fa" : "#e2e8f0"}
                  strokeWidth={isSelected ? 2 : 1}
                  className="shadow-sm"
                />
                {/* Node Title */}
                <text
                  x="10"
                  y="20"
                  fontSize="10.5"
                  fontWeight="600"
                  fontFamily="JetBrains Mono, monospace"
                  fill="#0f172a"
                >
                  {n.id.length > 20 ? `${n.id.slice(0, 19)}…` : n.id}
                </text>
                {/* Visits / Landmark Count */}
                <text
                  x="10"
                  y="38"
                  fontSize="9.5"
                  fill="#64748b"
                  fontFamily="Inter, sans-serif"
                >
                  {n.visits != null ? `${n.visits} visits` : n.title ?? "State"}
                </text>
                {n.status && (
                  <circle
                    cx={n.w - 12}
                    cy="14"
                    r="4"
                    fill={n.status === "verified" ? "#16a34a" : "#3b82f6"}
                  />
                )}
              </g>
            );
          })}
        </svg>
      </div>

      {selectedNode && (
        <div className="mt-3 rounded-md border border-blue-100 bg-blue-50/50 p-2.5 text-xs text-slate-700">
          <div className="font-semibold text-blue-900">{selectedNode.id}</div>
          <div className="mt-1 flex flex-wrap gap-x-4 text-[11px] text-slate-600">
            <span>Visits: <strong>{selectedNode.visits ?? 1}</strong></span>
            <span>Title: <strong>{selectedNode.title || "—"}</strong></span>
            <span>
              Connected Routes:{" "}
              <strong>
                {layout.edges.filter((e) => e.from === selectedNode.id || e.to === selectedNode.id).length}
              </strong>
            </span>
          </div>
        </div>
      )}
    </div>
  );
}
