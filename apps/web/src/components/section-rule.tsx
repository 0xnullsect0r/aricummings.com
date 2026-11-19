// A section separator: 1px bone rule with the mono uppercase label sitting on
// it, label left, rule continuing right (per docs/design-system.md).

export function SectionRule({ label, id }: { label: string; id?: string }) {
  return (
    <div id={id} className="flex scroll-mt-24 items-center gap-3" aria-hidden="false">
      <h2 className="whitespace-nowrap font-mono text-[11px] uppercase tracking-[0.08em] text-bone-dim md:text-xs">
        &mdash; {label}
      </h2>
      <div className="h-px flex-1 bg-edge" aria-hidden="true" />
    </div>
  );
}
