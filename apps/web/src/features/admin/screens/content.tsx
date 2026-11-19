/** Content list: GET /api/admin/content — every document grouped by kind. */

import { useState } from "react";
import type { ReactElement } from "react";
import { api } from "../api";
import { useAuth } from "../auth";
import { fmtRelative } from "../format";
import { Link, useRouter } from "../router";
import { Btn, errorText, Input, Panel, Pill, ScreenLoader, TableWrap, useAsync } from "../ui";
import type { ContentList, ContentListItem } from "../types";

/** Display name for a document key: site → Site, page:home → Home, work:x → x. */
function docTitle(key: string): string {
  if (key === "site") return "Site";
  if (key === "page:home") return "Home";
  return key.startsWith("work:") ? key.slice("work:".length) : key;
}

function kindOf(key: string): "site" | "page" | "work" {
  if (key === "site") return "site";
  if (key.startsWith("page:")) return "page";
  return "work";
}

const SLUG_RE = /^[a-z0-9][a-z0-9-]*$/;

export function ContentListScreen(): ReactElement {
  const { can } = useAuth();
  const router = useRouter();
  const state = useAsync<ContentListItem[]>(() => api<ContentList>("GET", "/api/admin/content").then(r => r.documents), []);
  const [newSlug, setNewSlug] = useState("");
  const [slugError, setSlugError] = useState("");

  const createWork = (e: React.FormEvent): void => {
    e.preventDefault();
    const slug = newSlug.trim().toLowerCase();
    if (!SLUG_RE.test(slug)) {
      setSlugError("Lowercase letters, digits and dashes. The slug can never change once created.");
      return;
    }
    setSlugError("");
    setNewSlug("");
    router.navigate(`content/work:${slug}`);
  };

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }
  const docs = [...state.data].sort((a, b) => a.key.localeCompare(b.key));
  const groups: { label: string; rows: ContentListItem[] }[] = [
    { label: "SITE", rows: docs.filter((d) => kindOf(d.key) === "site") },
    { label: "PAGES", rows: docs.filter((d) => kindOf(d.key) === "page") },
    { label: "WORK", rows: docs.filter((d) => kindOf(d.key) === "work") },
  ];

  return (
    <>
      <div className="screen-head">
        <h1>CONTENT</h1>
        <span className="screen-sub">{docs.length} documents</span>
      </div>

      <div className="stack">
        {groups.map((group) =>
          group.rows.length === 0 ? null : (
            <Panel key={group.label} label={group.label}>
              <TableWrap>
                <thead>
                  <tr>
                    <th>Document</th>
                    <th>Key</th>
                    <th>State</th>
                    <th>Updated</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {group.rows.map((doc) => (
                    <tr key={doc.key}>
                      <td className="td-dim td-wrap">{docTitle(doc.key)}</td>
                      <td className="td-mono td-wrap">{doc.key}</td>
                      <td>
                        {doc.hasPublished ? <Pill tone="ok">LIVE</Pill> : <Pill tone="dim">UNPUBLISHED</Pill>}
                        {doc.draftDiffers ? (
                          <>
                            {" "}
                            <Pill tone="warn">DRAFT</Pill>
                          </>
                        ) : null}
                      </td>
                      <td className="td-mono">
                        {fmtRelative(doc.updatedAt)}
                      </td>
                      <td>
                        <Link className="btn btn-ghost btn-small" to={`content/${encodeURIComponent(doc.key)}`}>
                          EDIT
                        </Link>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </TableWrap>
            </Panel>
          ),
        )}

        <Panel label="NEW WORK PAGE">
          <form onSubmit={createWork}>
            <div className="media-pick">
              <label className="field-label" htmlFor="new-slug" style={{ margin: 0 }}>
                work:
              </label>
              <Input
                id="new-slug"
                className="input-mono"
                placeholder="slug"
                value={newSlug}
                onChange={(e) => setNewSlug(e.target.value)}
              />
              <Btn type="submit" variant="primary">
                CREATE
              </Btn>
            </div>
            {slugError !== "" ? (
              <p className="field-error" role="alert">
                {slugError}
              </p>
            ) : (
              <p className="field-hint">Reserved slugs (admin, api, media, account, checkout, cart) are refused by the server.</p>
            )}
          </form>
        </Panel>

        {can("content.publish") ? null : (
          <p className="td-muted" style={{ margin: 0 }}>
            You can edit drafts; publishing needs the content.publish permission.
          </p>
        )}
      </div>
    </>
  );
}
