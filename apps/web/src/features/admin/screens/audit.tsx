/**
 * Audit log: GET /api/admin/audit?before=&limit= — cursor-paged backwards in
 * time, with a client-side filter over the loaded rows (actor / action /
 * target substring). "Load older" walks the nextBefore cursor.
 */

import { useEffect, useMemo, useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError, query } from "../api";
import { fmtDate } from "../format";
import { Btn, Input, Panel, ScreenLoader, TableWrap } from "../ui";
import type { AuditEntry, AuditPage } from "../types";

const PAGE_SIZE = 50;

export function AuditScreen(): ReactElement {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [nextBefore, setNextBefore] = useState<number | null>(null);
  const [initial, setInitial] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState("");

  const loadPage = async (before: number | null): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const qs = query({ before: before === null ? undefined : String(before), limit: PAGE_SIZE });
      const page = await api<AuditPage>("GET", "/api/admin/audit", undefined, { query: qs });
      setEntries((current) => (before === null ? page.items : [...current, ...page.items]));
      setNextBefore(page.next);
      setInitial(false);
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (${err.code})` : "Could not load the audit log.");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadPage(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const shown = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (needle === "") return entries;
    return entries.filter((e) =>
      [e.actorEmail ?? "", e.action, e.target ?? "", e.detail ?? ""].some((field) => field.toLowerCase().includes(needle)),
    );
  }, [entries, filter]);

  if (initial) {
    return (
      <ScreenLoader error={error} loading={loading} onRetry={() => void loadPage(null)}>
        <span />
      </ScreenLoader>
    );
  }

  return (
    <>
      <div className="screen-head">
        <h1>AUDIT LOG</h1>
        <span className="screen-sub">{entries.length} entries loaded</span>
      </div>

      <div className="filter-row">
        <Input
          type="search"
          aria-label="FILTER LOADED ENTRIES"
          placeholder="filter actor / action / target / detail"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        <Btn variant="ghost" onClick={() => void loadPage(null)} disabled={loading}>
          RELOAD LATEST
        </Btn>
      </div>

      {error !== null ? (
        <div className="failure" role="alert">
          <p className="failure-title">FAILED TO LOAD</p>
          <p className="failure-reason">{error}</p>
          <Btn variant="ghost" onClick={() => void loadPage(null)}>
            RETRY
          </Btn>
        </div>
      ) : null}

      <Panel label="ENTRIES">
        {shown.length === 0 && !loading ? (
          <p className="empty">{filter === "" ? "The audit log is empty." : "Nothing loaded matches the filter."}</p>
        ) : (
          <TableWrap>
            <thead>
              <tr>
                <th>Time</th>
                <th>Actor</th>
                <th>Action</th>
                <th>Target</th>
                <th>Detail</th>
                <th>IP</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((entry) => (
                <tr key={entry.id}>
                  <td className="td-mono">{fmtDate(entry.at)}</td>
                  <td className="td-mono td-wrap">{entry.actor}</td>
                  <td className="td-mono td-wrap">{entry.action}</td>
                  <td className="td-mono td-wrap">{entry.target ?? "—"}</td>
                  <td className="td-dim td-wrap">{entry.detail ?? "—"}</td>
                  <td className="td-mono">{entry.ip ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </TableWrap>
        )}
        {loading ? (
          <div className="state-block" role="status">
            <span className="spinner" aria-hidden="true" />
            <span className="mono-caps">LOADING</span>
          </div>
        ) : null}
        {nextBefore !== null ? (
          <div style={{ padding: 16 }}>
            <Btn variant="ghost" disabled={loading} onClick={() => void loadPage(nextBefore)}>
              LOAD OLDER
            </Btn>
          </div>
        ) : (
          <p className="empty">Beginning of the log. The pruner never touches it.</p>
        )}
      </Panel>
    </>
  );
}
