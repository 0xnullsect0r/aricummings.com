import type { Metadata } from "next";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Not found",
  robots: { index: false, follow: false },
};

// Real 404 route. src/proxy.ts rewrites unknown paths here with status 404
// (a page that calls notFound() only renders in the browser, so this route is
// what actually serves the 404).
export default function NotFoundPage() {
  return (
    <section data-track="NOT FOUND" className="mx-auto max-w-[72rem] px-4 py-24 md:px-6">
      <p className="font-mono text-[11px] uppercase tracking-[0.08em] text-muted md:text-xs">
        ERROR 404
      </p>
      <h1 className="mt-3 font-mono text-[clamp(2.75rem,7vw,5.5rem)] font-bold uppercase leading-[0.95] text-bone">
        NOT FOUND
      </h1>
      <p className="mt-6 max-w-[65ch] text-bone-dim">
        No page exists at this address. It may have moved or never existed.
      </p>
      <p className="mt-8 flex flex-wrap gap-x-6">
        <a
          href="/"
          className="inline-flex min-h-[44px] items-center font-mono text-xs uppercase tracking-[0.08em] text-accent-bright hover:underline"
        >
          &#8592; Home
        </a>
        <a
          href="/#work"
          className="inline-flex min-h-[44px] items-center font-mono text-xs uppercase tracking-[0.08em] text-bone-dim hover:text-accent-bright"
        >
          Work index
        </a>
      </p>
    </section>
  );
}
