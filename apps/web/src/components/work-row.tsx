import type { WorkDoc } from "@/lib/content";

// One numbered row of the work index: number, mono name, one-line descriptor,
// tech tags right, arrow. Hover fills raise2 and the arrow turns accent.

export function WorkRow({
  slug,
  index,
  doc,
  tag,
}: {
  slug: string;
  index: number;
  doc: WorkDoc;
  tag?: string;
}) {
  const tech = (doc.tech ?? []).slice(0, 4);
  return (
    <a
      href={`/work/${slug}`}
      className="group flex min-h-[44px] items-center gap-4 border-t border-line px-2 py-4 hover:bg-raise2 md:px-3"
    >
      <span className="w-8 shrink-0 font-mono text-xs text-muted">
        {String(index + 1).padStart(2, "0")}
      </span>
      <span className="min-w-0 flex-1">
        <span className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          <span className="font-mono text-base font-bold text-bone md:text-lg">{doc.title}</span>
          {doc.status && (
            <span className="font-mono text-[10px] uppercase tracking-[0.08em] text-muted">
              &mdash; {doc.status}
            </span>
          )}
          {tag && (
            <span className="border border-line-strong px-1.5 py-0.5 font-mono text-[10px] uppercase tracking-[0.08em] text-muted">
              {tag}
            </span>
          )}
        </span>
        {doc.summary && (
          <span className="mt-0.5 block truncate font-sans text-sm text-bone-dim">{doc.summary}</span>
        )}
      </span>
      {tech.length > 0 && (
        <span className="hidden shrink-0 flex-wrap justify-end gap-1.5 lg:flex">
          {tech.map((item) => (
            <span
              key={item}
              className="border border-line px-1.5 py-0.5 font-mono text-[10px] uppercase tracking-[0.08em] text-muted"
            >
              {item}
            </span>
          ))}
        </span>
      )}
      <span aria-hidden="true" className="shrink-0 font-mono text-base text-bone group-hover:text-accent">
        &#8594;
      </span>
    </a>
  );
}
