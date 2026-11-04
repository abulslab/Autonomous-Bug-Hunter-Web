"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

export const API = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5280";

// Optional: when the API is started with an API key, store it once in this browser (localStorage "bughunter_api_key").
export function getToken() {
  return typeof window === "undefined" ? null : localStorage.getItem("bughunter_api_key");
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public body?: any) {
    super(message);
  }
}

export async function api<T = any>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers: Record<string, string> = { ...(init.headers as any) };
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  if (init.json !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(init.json);
  }
  const res = await fetch(`${API}/api${path}`, { ...init, headers });
  const text = await res.text();
  const body = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const detail = body?.errors?.join("; ") ?? body?.detail ?? body?.title ?? res.statusText;
    throw new ApiError(res.status, detail, body);
  }
  return body as T;
}

// Downloads an API response as a file (the API key, when set, is sent as a header, so a plain link would not work).
export async function download(path: string, filename: string) {
  const token = getToken();
  const res = await fetch(`${API}/api${path}`, { headers: token ? { "x-api-key": token } : {} });
  if (!res.ok) throw new ApiError(res.status, `Download failed (${res.status})`);
  const url = URL.createObjectURL(await res.blob());
  const a = Object.assign(document.createElement("a"), { href: url, download: filename });
  a.click();
  URL.revokeObjectURL(url);
}

export function useApi<T = any>(path: string | null, opts: { refetch?: number } = {}) {
  return useQuery<T>({
    queryKey: [path],
    queryFn: () => api<T>(path!),
    enabled: !!path,
    refetchInterval: opts.refetch,
  });
}

export function useAction<TVars = any, TRes = any>(
  fn: (v: TVars) => Promise<TRes>,
  invalidate: string[] = [],
) {
  const qc = useQueryClient();
  return useMutation<TRes, ApiError, TVars>({
    mutationFn: fn,
    onSuccess: () => invalidate.forEach((k) => qc.invalidateQueries({ predicate: (q) => String(q.queryKey[0]).startsWith(k) })),
  });
}

export const fmt = {
  usd: (n?: number | null) => (n == null ? "-" : `$${n.toFixed(n < 1 ? 4 : 2)}`),
  pct: (n?: number | null) => (n == null ? "-" : `${Math.round(n * 100)}%`),
  num: (n?: number | null) => (n == null ? "-" : n.toLocaleString()),
  ms: (n?: number | null) => (n == null ? "-" : n >= 1000 ? `${(n / 1000).toFixed(1)}s` : `${n}ms`),
  date: (s?: string | null) => (s ? new Date(s.endsWith("Z") ? s : s + "Z").toLocaleString() : "-"),
};
