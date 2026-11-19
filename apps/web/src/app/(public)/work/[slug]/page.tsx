import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { JsonLd } from "@/components/json-ld";
import { MediaImage } from "@/components/media-image";
import { SectionRule } from "@/components/section-rule";
import {
  PreviewExpiredError,
  readDoc,
  type SiteDoc,
  type Snapshot,
  type WorkDoc,
  type WorkSection,
} from "@/lib/content";
import { requestSnapshot } from "@/lib/request-snapshot";
import { siteOrigin } from "@/lib/seo";

export const dynamic = "force-dynamic";

function workKey(slug: string): string {
  return `work:${slug}`;
}

export async function generateMetadata({
  params,
}: {
  params: Promise<{ slug: string }>;
}): Promise<Metadata> {
  const { slug } = await params;
  try {
    const { snapshot } = await requestSnapshot();
    const site = readDoc<SiteDoc>(snapshot, "site");
    const work = readDoc<WorkDoc>(snapshot, workKey(slug));
    const base = siteOrigin(site);
    const siteName = site.name ?? "Ari Cummings";
    if (!work.title) return {};
    const title = `${work.title} — ${siteName}`;
    return {
      title,
      description: work.summary || undefined,
      alternates: { canonical: `${base}/work/${slug}` },
      openGraph: { title, description: work.summary || undefined, url: `${base}/work/${slug}`, type: "website" },
    };
  } catch {
    return {};
  }
}

export default async function WorkPage({ params }: { params: Promise<{ slug: string }> }) {
  const { slug } = await params;
  let snapshot: Snapshot;
  let nonce: string | undefined;
  let preview = false;
  try {
    ({ snapshot, nonce, preview } = await requestSnapshot());
  } catch (error) {
    if (error instanceof PreviewExpiredError) {
      return (
        <section data-track="PREVIEW EXPIRED" className="mx-auto max-w-[72rem] px-4 py-24 md:px-6">
          <h1 className="font-mono text-2xl font-bold text-bone md:text-4xl">PREVIEW EXPIRED</h1>
          <p className="mt-4 max-w-[65ch] text-bone-dim">
            This preview link has expired or is invalid.
          </p>
        </section>
      );
    }
    throw error;
  }

  const site = readDoc<SiteDoc>(snapshot, "site");
  const work = readDoc<WorkDoc>(snapshot, workKey(slug));
  if (!work.title) notFound();

  const media = snapshot.media ?? {};
  const year = work.year !== undefined ? String(work.year) : "";
  const tech = work.tech ?? [];
  const links = (work.links ?? []).filter((link) => link.url);
  const repoUrl = work.repo?.url;
  const repoLicense = work.repo?.license;
  const sections = work.sections ?? [];
  const backLink =
    "inline-flex min-h-[44px] items-center font-mono text-[11px] uppercase tracking-[0.08em] text-muted hover:text-accent-bright";
  const externalLink =
    "inline-flex min-h-[44px] items-center gap-2 font-mono text-sm text-accent-bright hover:underline";

  return (
    <article className="mx-auto max-w-[72rem] px-4 pb-20 pt-10 md:px-6 md:pb-24 md:pt-14">
      {preview && (
        <p
          role="note"
          className="mb-6 border border-accent px-3 py-2 font-mono text-[11px] uppercase tracking-[0.08em] text-accent-bright"
        >
          Preview — unpublished draft
        </p>
      )}
      <JsonLd
        nonce={nonce}
        data={{
          "@context": "https://schema.org",
          "@type": "CreativeWork",
          name: work.title,
          ...(work.summary ? { description: work.summary } : {}),
          ...(year ? { dateCreated: year } : {}),
          author: { "@type": "Person", name: site.name ?? "Ari Cummings" },
          ...(site.githubHandle ? { url: `https://github.com/${site.githubHandle}` } : {}),
        }}
      />

      <a href="/" className={backLink}>
        &#8592; Index
      </a>

      <header className="mt-4 border-b border-line pb-8" data-track={work.title}>
        <h1 className="font-mono text-[clamp(1.5rem,3vw,2.25rem)] font-bold leading-tight text-bone">
          {work.title}
        </h1>
        <p className="mt-3 flex flex-wrap gap-x-4 gap-y-1 font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
          {work.status && <span className="text-accent-bright">{work.status}</span>}
          <span>{work.category === "client" ? "Client work" : "Open source"}</span>
          {year && <span>{year}</span>}
        </p>
        {work.summary && (
          <p className="mt-5 max-w-[65ch] text-[1.0625rem] leading-[1.65] text-bone-dim">
            {work.summary}
          </p>
        )}
        {(links.length > 0 || repoUrl) && (
          <p className="mt-5 flex flex-wrap gap-x-6 gap-y-1">
            {repoUrl && (
              <a href={repoUrl} target="_blank" rel="noopener noreferrer" className={externalLink}>
                Repository &#8599;{repoLicense ? <span className="text-muted">{repoLicense}</span> : null}
              </a>
            )}
            {links.map((link) => (
              <a
                key={link.url}
                href={link.url}
                target="_blank"
                rel="noopener noreferrer"
                className={externalLink}
              >
                {link.label || link.url} &#8599;
              </a>
            ))}
          </p>
        )}
        {tech.length > 0 && (
          <p className="mt-5 flex flex-wrap gap-1.5">
            {tech.map((item) => (
              <span
                key={item}
                className="border border-line px-1.5 py-0.5 font-mono text-[10px] uppercase tracking-[0.08em] text-muted"
              >
                {item}
              </span>
            ))}
          </p>
        )}
      </header>

      {sections.map((section: WorkSection, index) => {
        const type = section.type ?? "";
        if (type === "text") {
          if (!section.html && !section.heading) return null;
          return (
            <section
              key={index}
              data-track={section.heading || "TEXT"}
              className="mt-10 first:mt-10"
            >
              {section.heading && (
                <SectionRule label={section.heading} />
              )}
              {section.html && (
                <div
                  className="rich mt-4 max-w-[65ch] text-[1.0625rem] leading-[1.65]"
                  dangerouslySetInnerHTML={{ __html: section.html }}
                />
              )}
            </section>
          );
        }
        if (type === "photos") {
          const items = (section.items ?? []).filter((item) => item.image);
          if (items.length === 0) return null;
          return (
            <section key={index} data-track={section.heading || "PHOTOS"} className="mt-10">
              {section.heading && <SectionRule label={section.heading} />}
              <div
                className={`grid gap-px border border-line bg-line ${section.heading ? "mt-4" : ""} ${
                  items.length > 1 ? "sm:grid-cols-2" : ""
                }`}
              >
                {items.map((item, itemIndex) => (
                  <MediaImage
                    key={`${item.image}-${itemIndex}`}
                    id={item.image}
                    media={media}
                    alt={item.alt}
                    caption={item.caption}
                  />
                ))}
              </div>
            </section>
          );
        }
        if (type === "link") {
          if (!section.url) return null;
          return (
            <section key={index} data-track={section.heading || "LINK"} className="mt-10">
              <a
                href={section.url}
                target="_blank"
                rel="noopener noreferrer"
                className="flex min-h-[44px] items-center justify-between gap-4 border border-line px-4 py-3 font-mono text-sm text-bone hover:bg-raise2"
              >
                <span>{section.label || section.url}</span>
                <span aria-hidden="true" className="text-muted">
                  &#8599;
                </span>
              </a>
            </section>
          );
        }
        return null;
      })}
    </article>
  );
}
