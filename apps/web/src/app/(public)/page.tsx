import type { Metadata } from "next";
import { ContactForm } from "@/components/contact-form";
import { GithubStrip } from "@/components/github-strip";
import { JsonLd } from "@/components/json-ld";
import { SectionRule } from "@/components/section-rule";
import { StickyCta } from "@/components/sticky-cta";
import { WorkRow } from "@/components/work-row";
import {
  PreviewExpiredError,
  orderedWork,
  readDoc,
  type HomeAbout,
  type HomeDoc,
  type HomeFact,
  type SiteDoc,
  type Snapshot,
} from "@/lib/content";
import { requestSnapshot } from "@/lib/request-snapshot";
import { ogImageUrl, siteOrigin } from "@/lib/seo";

export const dynamic = "force-dynamic";

export async function generateMetadata(): Promise<Metadata> {
  try {
    const { snapshot } = await requestSnapshot();
    const site = readDoc<SiteDoc>(snapshot, "site");
    const home = readDoc<HomeDoc>(snapshot, "page:home");
    const base = siteOrigin(site);
    const name = site.name ?? "Ari Cummings";
    const tagline = site.tagline ?? "";
    const title = site.seo?.title ?? (tagline ? `${name} — ${tagline}` : name);
    const about = home.about as HomeAbout | undefined;
    const description =
      site.seo?.description ?? home.hero?.intro ?? about?.paras?.find((p) => typeof p === "string") ?? tagline;
    const og = ogImageUrl(snapshot, site);
    return {
      title,
      description: description || undefined,
      alternates: { canonical: `${base}/` },
      openGraph: {
        title,
        description: description || undefined,
        url: `${base}/`,
        type: "website",
        ...(og ? { images: [{ url: og, width: 1200, height: 630 }] } : {}),
      },
    };
  } catch {
    return {};
  }
}

function Failure({ title, body }: { title: string; body: string }) {
  return (
    <section data-track={title} className="mx-auto max-w-[72rem] px-4 py-24 md:px-6">
      <p className="font-mono text-[11px] uppercase tracking-[0.08em] text-muted">{title}</p>
      <h1 className="mt-3 font-mono text-2xl font-bold text-bone md:text-4xl">{title}</h1>
      <p className="mt-4 max-w-[65ch] text-bone-dim">{body}</p>
    </section>
  );
}

export default async function HomePage() {
  let snapshot: Snapshot;
  let nonce: string | undefined;
  let preview = false;
  try {
    ({ snapshot, nonce, preview } = await requestSnapshot());
  } catch (error) {
    if (error instanceof PreviewExpiredError) {
      return <Failure title="PREVIEW EXPIRED" body="This preview link has expired or is invalid." />;
    }
    return (
      <Failure
        title="SITE UNAVAILABLE"
        body="The content backend did not answer and no cached copy exists. Try again in a moment."
      />
    );
  }

  const site = readDoc<SiteDoc>(snapshot, "site");
  const home = readDoc<HomeDoc>(snapshot, "page:home");
  const name = site.name ?? "Ari Cummings";
  const roles = home.hero?.roles?.filter(Boolean) ?? (site.tagline ? [site.tagline] : []);
  const works = orderedWork(snapshot);
  const selected = works.filter((entry) => entry.doc.category !== "client");
  const clientWorks = works.filter((entry) => entry.doc.category === "client");
  const clientTag = (home.clientTag ?? "Artistech Endeavors").toUpperCase();
  const alsoBuilding = home.alsoBuilding ?? [];
  const stackGroups = (home.stack ?? []).filter((group) => group.items?.length);
  const about = home.about;
  const facts = about?.facts ?? [];

  const person = {
    "@context": "https://schema.org",
    "@type": "Person",
    name,
    url: `${siteOrigin(site)}/`,
    ...(roles.length > 0 ? { jobTitle: roles.join(", ") } : {}),
    ...(site.githubHandle ? { sameAs: [`https://github.com/${site.githubHandle}`, "https://nullsect0r.dev"] } : { sameAs: ["https://nullsect0r.dev"] }),
    ...(site.location ? { address: site.location } : {}),
  };

  return (
    <div className="mx-auto max-w-[72rem] px-4 pb-20 md:px-6 md:pb-24">
      {preview && (
        <p
          role="note"
          className="mt-4 border border-accent px-3 py-2 font-mono text-[11px] uppercase tracking-[0.08em] text-accent-bright"
        >
          Preview — unpublished draft
        </p>
      )}
      <JsonLd data={person} nonce={nonce} />

      {/* HERO */}
      <section data-track="HERO" className="pt-14 md:pt-20">
        {home.hero?.kicker && (
          <p className="font-mono text-[11px] uppercase tracking-[0.08em] text-muted md:text-xs">
            {home.hero.kicker}
          </p>
        )}
        <h1 className="mt-3 font-mono text-[clamp(2.75rem,7vw,5.5rem)] font-bold uppercase leading-[0.95] text-bone">
          {name}
        </h1>
        {roles.length > 0 && (
          <p className="mt-5 font-mono text-sm uppercase tracking-[0.08em] text-accent-bright md:text-base">
            {roles.join(" / ").toUpperCase()}
          </p>
        )}
        {(home.hero?.lines ?? []).map((line) => (
          <p
            key={line}
            className="mt-2 font-mono text-[clamp(1.5rem,3vw,2.25rem)] font-bold leading-tight text-bone"
          >
            {line}
          </p>
        ))}
        {home.hero?.intro && (
          <p className="mt-6 max-w-[65ch] leading-[1.65] text-bone-dim">{home.hero.intro}</p>
        )}
      </section>

      {/* ABOUT */}
      {((about?.paras?.length ?? 0) > 0 || facts.length > 0) && (
        <section data-track="ABOUT" className="mt-16 scroll-mt-24 md:mt-24">
          <SectionRule label="ABOUT" id="about" />
          <div className="mt-6 grid gap-px border border-line bg-line md:grid-cols-12">
            <div className="bg-ground p-4 md:col-span-7 md:p-6">
              {(about?.paras ?? []).map((paragraph) => (
                <p key={paragraph.slice(0, 40)} className="mb-4 max-w-[65ch] text-bone-dim last:mb-0">
                  {paragraph}
                </p>
              ))}
            </div>
            <div className="bg-ground p-4 md:col-span-5 md:p-6">
              <dl>
                {facts.map((fact, index) => {
                  const isPair = typeof fact === "object" && fact !== null;
                  const pair = fact as HomeFact;
                  const label = isPair ? pair.label : undefined;
                  const value = isPair ? pair.value : String(fact);
                  if (!value && !label) return null;
                  return (
                    <div
                      key={`${label ?? value ?? "fact"}-${index}`}
                      className={index === 0 ? "pb-3" : "border-t border-line py-3"}
                    >
                      {label && (
                        <dt className="font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
                          {label}
                        </dt>
                      )}
                      {value && (
                        <dd className={label ? "mt-1 font-mono text-sm text-bone" : "font-mono text-sm text-bone-dim"}>
                          {value}
                        </dd>
                      )}
                    </div>
                  );
                })}
              </dl>
            </div>
          </div>
        </section>
      )}

      {/* SELECTED WORK */}
      {selected.length > 0 && (
        <section data-track="SELECTED WORK" className="mt-16 scroll-mt-24 md:mt-24">
          <SectionRule label="SELECTED WORK" id="work" />
          <div className="mt-6 border-b border-line">
            {selected.map((entry, index) => (
              <WorkRow
                key={entry.slug}
                slug={entry.slug}
                index={index}
                doc={entry.doc}
              />
            ))}
          </div>
        </section>
      )}

      {/* CLIENT WORK */}
      {clientWorks.length > 0 && (
        <section data-track="CLIENT WORK" className="mt-16 scroll-mt-24 md:mt-24">
          <SectionRule label="CLIENT WORK" id="client-work" />
          {home.clientIntro && (
            <p className="mt-6 max-w-[65ch] text-bone-dim">{home.clientIntro}</p>
          )}
          <div className="mt-4 border-b border-line md:mt-6">
            {clientWorks.map((entry, index) => (
              <WorkRow
                key={entry.slug}
                slug={entry.slug}
                index={index}
                doc={entry.doc}
                tag={clientTag}
              />
            ))}
          </div>
        </section>
      )}

      {/* STACK */}
      {stackGroups.length > 0 && (
        <section data-track="STACK" className="mt-16 scroll-mt-24 md:mt-24">
          <SectionRule label="STACK" id="stack" />
          <div className="mt-6 grid gap-px border border-line bg-line md:grid-cols-2 lg:grid-cols-4">
            {stackGroups.map((group, index) => (
              <div key={group.label ?? index} className="bg-ground p-4 md:p-5">
                <h3 className="font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
                  {group.label ?? "STACK"}
                </h3>
                <ul className="mt-3 space-y-1">
                  {(group.items ?? []).map((item) => (
                    <li key={item} className="font-mono text-sm text-bone-dim">
                      {item}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </section>
      )}

      {/* ALSO BUILDING */}
      {alsoBuilding.length > 0 && (
        <section data-track="ALSO BUILDING" className="mt-16 scroll-mt-24 md:mt-24">
          <SectionRule label="ALSO BUILDING" id="also" />
          <div className="mt-6 border-b border-line">
            {alsoBuilding.map((item, index) => (
              <div
                key={`${item.name}-${index}`}
                className="flex min-h-[44px] items-center gap-3 border-t border-line py-2.5"
              >
                <span className="w-8 shrink-0 font-mono text-xs text-muted">
                  {String(index + 1).padStart(2, "0")}
                </span>
                {item.url ? (
                  <a
                    href={item.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="font-mono text-sm text-bone hover:text-accent-bright"
                  >
                    {item.name} &#8599;
                  </a>
                ) : (
                  <span className="font-mono text-sm text-bone-dim">{item.name}</span>
                )}
                {item.note && (
                  <span className="ml-auto hidden truncate pl-4 font-sans text-sm text-muted sm:block">
                    {item.note}
                  </span>
                )}
              </div>
            ))}
          </div>
        </section>
      )}

      {/* GITHUB — the signature moment */}
      <section className="mt-16 md:mt-24">
        <GithubStrip github={snapshot.github} />
      </section>

      {/* CONTACT */}
      <section data-track="CONTACT" className="mt-16 scroll-mt-24 md:mt-24">
        <SectionRule label="CONTACT" id="contact" />
        <div className="mt-6 grid gap-px border border-line bg-line md:grid-cols-12">
          <div className="bg-ground p-4 md:col-span-5 md:p-6">
            <p className="max-w-[45ch] text-bone-dim">
              Projects, contract work, or questions about anything on this site.
              Messages go straight to the inbox behind this form.
            </p>
            {site.location && (
              <p className="mt-5 font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
                {site.location}
              </p>
            )}
            {site.contactEmail && (
              <a
                href={`mailto:${site.contactEmail}`}
                className="mt-2 inline-block font-mono text-sm text-accent-bright hover:underline"
              >
                {site.contactEmail}
              </a>
            )}
          </div>
          <div className="bg-ground p-4 md:col-span-7 md:p-6">
            <ContactForm />
          </div>
        </div>
      </section>

      <div className="mt-16 md:mt-20">
        <StickyCta />
      </div>
    </div>
  );
}
