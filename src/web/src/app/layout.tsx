import type { Metadata } from "next";
import "./globals.css";
import { Providers } from "@/lib/providers";
import { Nav } from "@/components/nav";
import { Header } from "@/components/header";

export const metadata: Metadata = {
  title: "Autonomous Bug Hunter",
  description: "Explores a web app in a real browser and reports only bugs it can prove, with evidence, replay and a Playwright test for each.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="bg-slate-50/60 text-slate-900 antialiased min-h-screen flex flex-col">
        <Providers>
          <Header />
          <div className="mx-auto w-full max-w-7xl px-4 sm:px-6 lg:px-8 py-6 flex-1 flex flex-col md:flex-row gap-6 lg:gap-8 items-start">
            <Nav />
            <main className="min-w-0 flex-1 w-full">
              {children}
            </main>
          </div>
        </Providers>
      </body>
    </html>
  );
}
