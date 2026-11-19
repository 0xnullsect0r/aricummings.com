// Sticky phone call-to-action: one main action on this site (contact), so it
// is worth it on phones and absent from desktop.

export function StickyCta() {
  return (
    <>
      <div className="fixed inset-x-0 bottom-0 z-40 md:hidden" data-track="STICKY CTA">
        <a
          href="/#contact"
          className="flex h-12 items-center justify-center border-t border-edge bg-accent font-mono text-xs font-bold uppercase tracking-[0.08em] text-accent-ink"
        >
          Contact &#8594;
        </a>
      </div>
      <div className="h-12 md:hidden" aria-hidden="true" />
    </>
  );
}
