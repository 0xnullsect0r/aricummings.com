/** Dashboard: GET /api/admin/overview — unread messages, last publish, 7-day analytics, job states. */

import type { ReactElement } from "react";
import { api } from "../api";
import { useAuth } from "../auth";
import { fmtDate, fmtNum, fmtRelative } from "../format";
import { Link } from "../router";
import { errorText, Panel, Pill, ScreenLoader, TableWrap, useAsync } from "../ui";
import type { Overview } from "../types";

export function DashboardScreen(): ReactElement {
  const { can } = useAuth();
  const state = useAsync<Overview>(() => api<Overview>("GET", "/api/admin/overview"), []);

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }
  const d = state.data;

  return (
    <>
      <div className="screen-head">
        <h1>DASHBOARD</h1>
        <span className="screen-sub">as of {fmtDate(Date.now())}</span>
      </div>

      <div className="cards" style={{ marginBottom: 24 }}>
        <div className="card">
          <span className="card-num">{fmtNum(d.messages.unread)}</span>
          <span className="card-label">UNREAD MESSAGES</span>
        </div>
        <div className="card">
          <span className="card-num">{d.analytics.visitors7d === 0 ? "0" : fmtNum(d.analytics.visitors7d)}</span>
          <span className="card-label">VISITORS / 7 DAYS</span>
        </div>
        <div className="card">
          <span className="card-num">{fmtNum(d.analytics.pageviews7d)}</span>
          <span className="card-label">PAGE VIEWS / 7 DAYS</span>
        </div>
        <div className="card">
          <span className="card-num">{fmtNum(d.analytics.conversions7d)}</span>
          <span className="card-label">CONVERSIONS / 7 DAYS</span>
        </div>
      </div>

      <div className="stack">
        {can("messages.read") ? (
          <Panel
            label="INBOX"
            actions={
              <Link to="messages" className="btn btn-ghost btn-small">
                OPEN MESSAGES
              </Link>
            }
          >
            <p className="td-dim" style={{ margin: 0 }}>
              {d.messages.unread === 0
                ? "No unread messages."
                : `${fmtNum(d.messages.unread)} unread message${d.messages.unread === 1 ? "" : "s"} waiting.`}
            </p>
          </Panel>
        ) : null}

        <Panel label="PUBLISHING">
          <ul className="kv-list">
            <li>
              <span className="kv-key">LAST PUBLISH</span>
              <span className="td-mono">
                {d.content.lastPublishedAt !== null
                  ? `${fmtRelative(d.content.lastPublishedAt)}${d.content.lastPublishedBy !== null && d.content.lastPublishedBy !== "" ? ` by ${d.content.lastPublishedBy}` : ""}`
                  : "never published"}
              </span>
            </li>
            <li>
              <span className="kv-key">DRAFTS WITH CHANGES</span>
              <span className="td-mono">{fmtNum(d.content.drafts)}</span>
            </li>
          </ul>
          {can("content.edit") ? (
            <div style={{ marginTop: 12 }}>
              <Link to="content" className="btn btn-ghost btn-small">
                OPEN CONTENT
              </Link>
            </div>
          ) : null}
        </Panel>

        <Panel label="JOBS">
          {d.jobs.length === 0 ? (
            <p className="empty">No background jobs registered.</p>
          ) : (
            <TableWrap>
              <thead>
                <tr>
                  <th>Job</th>
                  <th>State</th>
                  <th>Last run</th>
                  <th>Detail</th>
                </tr>
              </thead>
              <tbody>
                {d.jobs.map((job) => (
                  <tr key={job.name}>
                    <td className="td-mono">{job.name}</td>
                    <td>
                      {job.lastRunAt === null ? (
                        <Pill tone="dim">NEVER RUN</Pill>
                      ) : job.ok ? (
                        <Pill tone="ok">OK</Pill>
                      ) : (
                        <Pill tone="danger">FAILED</Pill>
                      )}
                    </td>
                    <td className="td-mono">{fmtRelative(job.lastRunAt)}</td>
                    <td className="td-dim td-wrap">{job.detail ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </TableWrap>
          )}
        </Panel>
      </div>
    </>
  );
}
