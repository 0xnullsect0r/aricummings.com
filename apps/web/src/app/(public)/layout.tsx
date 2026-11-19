import { Analytics } from "@/components/analytics";
import { Footer } from "@/components/footer";
import { Header } from "@/components/header";
import { getSnapshot, readDoc, type SiteDoc } from "@/lib/content";

export default async function PublicLayout({ children }: { children: React.ReactNode }) {
  let site: SiteDoc = {};
  try {
    site = readDoc<SiteDoc>(await getSnapshot(), "site");
  } catch {
    // Backend down with no last-good copy: the chrome still renders.
  }
  return (
    <>
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:bg-accent focus:px-3 focus:py-2 focus:font-mono focus:text-xs focus:font-bold focus:uppercase focus:tracking-[0.08em] focus:text-accent-ink"
      >
        Skip to content
      </a>
      <Header site={site} />
      <main id="main" className="flex-1">
        {children}
      </main>
      <Footer site={site} />
      <Analytics />
    </>
  );
}
