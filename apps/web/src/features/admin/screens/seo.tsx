/**
 * SEO: what a search engine would make of each live page, computed from the
 * published documents (GET /api/admin/content) plus the media index
 * (GET /api/admin/media) for missing-photo checks.
 *
 * Title = page title + " — " + site name, flagged past 60 characters.
 * Description flagged under 50 and past 160. noindex shown per page.
 */

import type { ReactElement } from "react";
import { api } from "../api";
import { errorText, Empty, Panel, Pill, ScreenLoader, TableWrap, useAsync } from "../ui";
import type { ContentList, ContentListItem, HomeDoc, MediaItem, SiteDoc, WorkDoc } from "../types";

interface SeoRow {
  path: string;
  title: string;
  description: string;
  noindex: boolean;
  missingMedia: string[];
}

function collectMediaIds(value: unknown, found: Set<string>): void {
  if (Array.isArray(value)) {
    for (const entry of value) collectMediaIds(entry, found);
    return;
  }
  if (value !== null && typeof value === "object") {
    const rec = value as Record<string, unknown>;
    if (typeof rec.image === "string" && /^[0-9a-f]{16}$/.test(rec.image)) found.add(rec.image);
    for (const child of Object.values(rec)) collectMediaIds(child, found);
  }
}

function buildRows(docs: ContentListItem[], mediaIds: Set<string>): SeoRow[] {
  const site = docs.find((d) => d.key === "site")?.published as SiteDoc | undefined | null;
  const siteName = site?.name ?? "";
  const defaults = site?.seo ?? {};
  const rows: SeoRow[] = [];

  const home = docs.find((d) => d.key === "page:home")?.published as HomeDoc | undefined | null;
  if (home !== null && home !== undefined) {
    const ids = new Set<string>();
    collectMediaIds(home, ids);
    rows.push({
      path: "/",
      title: defaults.title ?? siteName,
      description: defaults.description ?? "",
      noindex: defaults.noindex === true,
      missingMedia: [...ids].filter((id) => !mediaIds.has(id)),
    });
  }

  const works = docs
    .filter((d) => d.key.startsWith("work:") && d.hasPublished)
    .sort((a, b) => a.key.localeCompare(b.key));
  for (const doc of works) {
    const work = doc.published as WorkDoc;
    const slug = doc.key.slice("work:".length);
    const ids = new Set<string>();
    if (work !== null && work !== undefined) collectMediaIds(work, ids);
    const baseTitle = work?.seo?.title ?? work?.title ?? slug;
    const title = siteName === "" ? baseTitle : `${baseTitle} — ${siteName}`;
    rows.push({
      path: `/work/${slug}`,
      title,
      description: work?.seo?.description ?? work?.summary ?? "",
      noindex: work?.seo?.noindex === true || defaults.noindex === true,
      missingMedia: [...ids].filter((id) => !mediaIds.has(id)),
    });
  }
  return rows;
}

function cut(value: string, max: number): string {
  return value.length <= max ? value : `${value.slice(0, max)}…`;
}

export function SeoScreen(): ReactElement {
  const state = useAsync<{ rows: SeoRow[] }>(
    async () => {
      const [docs, media] = await Promise.all([
        api<ContentList>("GET", "/api/admin/content").then(r => r.documents),
        api<{ media: MediaItem[] }>("GET", "/api/admin/media").then(r => r.media),
      ]);
      return { rows: buildRows(docs, new Set(media.map((m) => m.id))) };
    },
    [],
  );

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }
  const rows = state.data.rows;

  return (
    <>
      <div className="screen-head">
        <h1>SEO</h1>
        <span className="screen-sub">live pages only — {rows.length} published</span>
      </div>

      <Panel label="SEARCH PREVIEW">
        {rows.length === 0 ? (
          <Empty label="Nothing is published yet." />
        ) : (
          <TableWrap>
            <thead>
              <tr>
                <th>Page</th>
                <th>Title (60)</th>
                <th>Len</th>
                <th>Description (50–160)</th>
                <th>Len</th>
                <th>Index</th>
                <th>Photos</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const titleLen = row.title.length;
                const descLen = row.description.length;
                return (
                  <tr key={row.path}>
                    <td className="td-mono td-wrap">{row.path}</td>
                    <td className="td-wrap">
                      {titleLen > 60 ? (
                        <>
                          <Pill tone="warn">LONG</Pill>{" "}
                        </>
                      ) : null}
                      {cut(row.title, 60)}
                    </td>
                    <td className="td-num">{String(titleLen)}</td>
                    <td className="td-wrap">
                      {row.description === "" ? (
                        <Pill tone="danger">MISSING</Pill>
                      ) : (
                        <>
                          {descLen < 50 ? (
                            <>
                              <Pill tone="warn">SHORT</Pill>{" "}
                            </>
                          ) : null}
                          {descLen > 160 ? (
                            <>
                              <Pill tone="warn">LONG</Pill>{" "}
                            </>
                          ) : null}
                          {cut(row.description, 160)}
                        </>
                      )}
                    </td>
                    <td className="td-num">{String(descLen)}</td>
                    <td>{row.noindex ? <Pill tone="danger">NOINDEX</Pill> : <Pill tone="ok">INDEX</Pill>}</td>
                    <td className="td-wrap">
                      {row.missingMedia.length === 0 ? (
                        <Pill tone="ok">OK</Pill>
                      ) : (
                        <Pill tone="danger">{`${String(row.missingMedia.length)} MISSING`}</Pill>
                      )}
                      {row.missingMedia.length > 0 ? (
                        <span className="td-muted"> {row.missingMedia.join(", ")}</span>
                      ) : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </TableWrap>
        )}
      </Panel>

      <Panel label="RULES">
        <ul className="kv-list">
          <li>
            <span className="kv-key">TITLE</span>
            <span className="td-dim">Shown with the site name; engines cut around 60 characters.</span>
          </li>
          <li>
            <span className="kv-key">DESCRIPTION</span>
            <span className="td-dim">Under 50 reads thin; past 160 gets cut.</span>
          </li>
          <li>
            <span className="kv-key">PHOTOS</span>
            <span className="td-dim">Media ids referenced by the published page but absent from the library.</span>
          </li>
        </ul>
      </Panel>
    </>
  );
}
