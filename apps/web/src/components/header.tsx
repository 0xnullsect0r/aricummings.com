import type { SiteDoc } from "@/lib/content";

export function Header({ site }: { site: SiteDoc }) {
  const name = (site.name ?? "ARI CUMMINGS").toUpperCase();
  const github = site.githubHandle ? `https://github.com/${site.githubHandle}` : null;
  const navLink =
    "flex h-11 items-center font-mono text-[11px] uppercase tracking-[0.08em] text-bone-dim hover:text-accent-bright md:text-xs";
  return (
    <header className="sticky top-0 z-40 border-y border-edge bg-ground">
      <div className="mx-auto flex h-14 max-w-[72rem] items-center justify-between px-4 md:h-16 md:px-6">
        <a
          href="/"
          className="font-mono text-sm font-bold uppercase tracking-[0.08em] text-bone md:text-base"
        >
          {name}
        </a>
        <nav aria-label="Primary" className="flex items-center gap-2 md:gap-6">
          <a href="/#work" className={navLink}>
            Work
          </a>
          <a href="/#contact" className={navLink}>
            Contact
          </a>
          {github && (
            <a href={github} target="_blank" rel="noopener noreferrer" className={navLink}>
              GitHub &#8599;
            </a>
          )}
        </nav>
      </div>
    </header>
  );
}
