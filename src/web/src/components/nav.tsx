"use client";
import clsx from "clsx";
import Link from "next/link";
import { usePathname } from "next/navigation";

const items = [
  { href: "/", label: "Hunts", match: (p: string) => p === "/" || (p.startsWith("/hunts/") && p !== "/hunts/new") },
  { href: "/hunts/new", label: "New hunt", match: (p: string) => p === "/hunts/new" },
  { href: "/projects", label: "Projects", match: (p: string) => p.startsWith("/projects") },
  { href: "/maps", label: "App maps", match: (p: string) => p.startsWith("/maps") },
  { href: "/grounding", label: "Grounding", match: (p: string) => p.startsWith("/grounding") },
  { href: "/skills", label: "Skills", match: (p: string) => p.startsWith("/skills") },
  { href: "/benchmarks", label: "Benchmarks", match: (p: string) => p.startsWith("/benchmarks") },
];

export function Nav() {
  const path = usePathname();

  return (
    <aside className="w-52 shrink-0 rounded-xl border border-slate-200 bg-white p-3 shadow-xs">
      <div className="mb-2 px-2 text-[11px] font-semibold uppercase tracking-wider text-slate-400">
        Navigation
      </div>
      <nav className="space-y-1">
        {items.map((i) => {
          const active = i.match(path);
          return (
            <Link
              key={i.href}
              href={i.href}
              className={clsx(
                "flex items-center rounded-lg px-3 py-2 text-sm font-medium transition-colors",
                active
                  ? "bg-blue-50 text-blue-700 shadow-2xs font-semibold"
                  : "text-slate-600 hover:bg-slate-50 hover:text-slate-900"
              )}
            >
              {i.label}
            </Link>
          );
        })}
      </nav>

    </aside>
  );
}
