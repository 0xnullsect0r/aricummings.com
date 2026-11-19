// SEO helpers. The site URL comes from the content snapshot (built from
// PUBLIC_ORIGIN by the API); the env var is only a fallback so one image can
// run on staging and production without a rebuild.

import type { SiteDoc, Snapshot } from "@/lib/content";

export function siteOrigin(site: SiteDoc): string {
  const fromDoc = site.seo?.url;
  if (fromDoc && /^https?:\/\//i.test(fromDoc)) return fromDoc.replace(/\/+$/, "");
  const fromEnv = process.env.PUBLIC_ORIGIN;
  if (fromEnv && /^https?:\/\//i.test(fromEnv)) return fromEnv.replace(/\/+$/, "");
  return "http://localhost:8130";
}

/** First media asset flagged og:true -> "<base>/media/<id>/og.jpg". */
export function ogImageUrl(snapshot: Snapshot, site: SiteDoc): string | null {
  const base = siteOrigin(site);
  const media = snapshot.media ?? {};
  for (const [id, asset] of Object.entries(media)) {
    if (asset?.og) return `${base}/media/${id}/og.jpg`;
  }
  return null;
}
