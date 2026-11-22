import type { SiteDoc } from "@/lib/content";

export function Footer({ site }: { site: SiteDoc }) {
  const year = new Date().getFullYear();
  const name = site.name ?? "Ari Cummings";
  const license = site.footerLicense ?? "AGPL-3.0";
  const link =
    "flex min-h-[44px] items-center hover:text-accent-bright font-mono text-xs uppercase tracking-[0.08em]";
  return (
    <footer className="mt-20 border-t border-edge md:mt-24" data-track="FOOTER">
      <div className="mx-auto flex max-w-[72rem] flex-col gap-3 px-4 py-6 font-mono text-xs uppercase tracking-[0.08em] text-muted md:flex-row md:items-center md:justify-between md:px-6">
        <p>
          &copy; {year} {name} &middot; site code {license}
        </p>
        <p className="flex flex-wrap gap-x-5">
          <a href="https://nullsect0r.dev" target="_blank" rel="noopener noreferrer" className={link}>
            nullsect0r.dev
          </a>
          {site.githubHandle && (
            <a
              href={`https://github.com/${site.githubHandle}`}
              target="_blank"
              rel="noopener noreferrer"
              className={link}
            >
              GitHub
            </a>
          )}
          {site.contactEmail && (
            <a href={`mailto:${site.contactEmail}`} className={link}>
              Email
            </a>
          )}
        </p>
      </div>
    </footer>
  );
}
