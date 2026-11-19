/**
 * Statistics: first-party analytics reports for a day range.
 * summary, pages, referrers, clicks, geo, funnel, weekly-email status, CSV export.
 * All endpoints take ?days=; CSV is a plain link the browser downloads with cookies.
 */

import { useState } from "react";
import type { ReactElement } from "react";
import { api } from "../api";
import { useAuth } from "../auth";
import { fmtDuration, fmtNum, fmtPct } from "../format";
import { errorText, Empty, Panel, Pill, ScreenLoader, TableWrap, useAsync } from "../ui";
import type { AnalyticsClick, AnalyticsGeoRow, AnalyticsPage, AnalyticsReferrer, AnalyticsSummary } from "../types";

interface All {
  summary: AnalyticsSummary;
  pages: AnalyticsPage[];
  referrers: AnalyticsReferrer[];
  clicks: AnalyticsClick[];
  geo: AnalyticsGeoRow[];
}

const RANGES = [7, 30, 90];

export function StatisticsScreen(): ReactElement {
  const { can } = useAuth();
  const [days, setDays] = useState(7);

  const state = useAsync<All>(
    async () => {
      const q = `?days=${String(days)}`;
      const [summary, pages, referrers, clicks, geo] = await Promise.all([
        api<AnalyticsSummary>("GET", "/api/admin/analytics/summary", undefined, { query: q }),
        api<AnalyticsPage[]>("GET", "/api/admin/analytics/pages", undefined, { query: q }),
        api<AnalyticsReferrer[]>("GET", "/api/admin/analytics/referrers", undefined, { query: q }),
        api<AnalyticsClick[]>("GET", "/api/admin/analytics/clicks", undefined, { query: q }),
        api<AnalyticsGeoRow[]>("GET", "/api/admin/analytics/geo", undefined, { query: q }),
      ]);
      return { summary, pages, referrers, clicks, geo };
    },
    [days],
  );

  const headline = (
    <div className="filter-row" role="group" aria-label="DAY RANGE">
      <div className="segmented">
        {RANGES.map((r) => (
          <button key={r} type="button" aria-pressed={days === r} onClick={() => setDays(r)}>
            {r} DAYS
          </button>
        ))}
      </div>
      {can("analytics.export") ? (
        <a className="btn btn-ghost btn-small" href={`/api/admin/analytics/export.csv?days=${String(days)}`}>
          EXPORT CSV
        </a>
      ) : null}
    </div>
  );

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }
  const { summary, pages, referrers, clicks, geo } = state.data;
  const maxFunnel = summary.funnel.reduce((m, s) => Math.max(m, s.count), 0);

  return (
    <>
      <div className="screen-head">
        <h1>STATISTICS</h1>
        <span className="screen-sub">last {days} days</span>
      </div>
      {headline}

      <div className="cards" style={{ marginBottom: 24 }}>
        <div className="card">
          <span className="card-num">{fmtNum(summary.visitors)}</span>
          <span className="card-label">VISITORS</span>
        </div>
        <div className="card">
          <span className="card-num">{fmtNum(summary.newVisitors)}</span>
          <span className="card-label">NEW VISITORS</span>
        </div>
        <div className="card">
          <span className="card-num">{fmtNum(summary.pageviews)}</span>
          <span className="card-label">PAGE VIEWS</span>
        </div>
        <div className="card">
          <span className="card-num">{fmtDuration(summary.activeMs)}</span>
          <span className="card-label">AVG ACTIVE / VISIT</span>
        </div>
        <div className="card">
          <span className="card-num">{fmtNum(summary.conversions)}</span>
          <span className="card-label">CONVERSIONS</span>
        </div>
      </div>

      <div className="stack">
        <Panel
          label="WEEKLY EMAIL"
          actions={
            summary.weeklyEmail !== null ? (
              summary.weeklyEmail.enabled ? (
                <Pill tone="ok">ENABLED</Pill>
              ) : (
                <Pill tone="dim">OFF</Pill>
              )
            ) : (
              <Pill tone="dim">UNKNOWN</Pill>
            )
          }
        >
          <p className="td-dim" style={{ margin: 0 }}>
            {summary.weeklyEmail === null
              ? "Weekly email status is not available."
              : summary.weeklyEmail.enabled
                ? "A weekly summary is emailed to owner accounts."
                : "The weekly summary email is switched off."}
          </p>
        </Panel>

        <Panel label="FUNNEL">
          {summary.funnel.length === 0 ? (
            <Empty label="No funnel steps recorded in this range." />
          ) : (
            summary.funnel.map((step) => (
              <div className="bar-row" key={step.label}>
                <span className="bar-label">{step.label}</span>
                <span className="bar-track">
                  <span
                    className="bar-fill"
                    style={{ width: maxFunnel === 0 ? "0%" : `${(step.count / maxFunnel) * 100}%`, display: "block" }}
                  />
                </span>
                <span className="bar-num">{fmtNum(step.count)}</span>
              </div>
            ))
          )}
        </Panel>

        <Panel label="PAGES">
          {pages.length === 0 ? (
            <Empty label="No page views in this range." />
          ) : (
            <TableWrap>
              <thead>
                <tr>
                  <th>Path</th>
                  <th>Views</th>
                  <th>Avg active</th>
                  <th>Scroll</th>
                </tr>
              </thead>
              <tbody>
                {pages.map((p) => (
                  <tr key={p.path}>
                    <td className="td-mono td-wrap">{p.path}</td>
                    <td className="td-num">{fmtNum(p.views)}</td>
                    <td className="td-num">{fmtDuration(p.activeMs)}</td>
                    <td className="td-num">{fmtPct(p.scrollPct)}</td>
                  </tr>
                ))}
              </tbody>
            </TableWrap>
          )}
        </Panel>

        <Panel label="REFERRERS">
          {referrers.length === 0 ? (
            <Empty label="No referrers in this range." />
          ) : (
            <TableWrap>
              <thead>
                <tr>
                  <th>Source</th>
                  <th>Visits</th>
                </tr>
              </thead>
              <tbody>
                {referrers.map((r) => (
                  <tr key={r.host}>
                    <td className="td-mono td-wrap">{r.host}</td>
                    <td className="td-num">{fmtNum(r.visits)}</td>
                  </tr>
                ))}
              </tbody>
            </TableWrap>
          )}
        </Panel>

        <Panel label="CLICKS — TOP LABELS AND TARGETS">
          {clicks.length === 0 ? (
            <Empty label="No clicks recorded in this range." />
          ) : (
            clicks.map((c) => (
              <div className="bar-row" key={`${c.label}|${c.target ?? ""}`}>
                <span className="bar-label">{c.label}</span>
                <span className="bar-track">
                  <span
                    className="bar-fill"
                    style={{
                      width: `${(c.count / (clicks[0]?.count ?? 1)) * 100}%`,
                      display: "block",
                    }}
                  />
                </span>
                <span className="bar-num">{fmtNum(c.count)}</span>
              </div>
            ))
          )}
        </Panel>

        <Panel label="GEOGRAPHY">
          {geo.length === 0 ? (
            <Empty label="No visits with a known location in this range." />
          ) : (
            <>
              <TableWrap>
                <thead>
                  <tr>
                    <th>Country</th>
                    <th>Region</th>
                    <th>City</th>
                    <th>Visits</th>
                  </tr>
                </thead>
                <tbody>
                  {geo.map((g, i) => (
                    <tr key={`${g.country ?? ""}|${g.region ?? ""}|${g.city ?? ""}|${String(i)}`}>
                      <td className="td-mono">{g.country ?? "—"}</td>
                      <td className="td-dim">{g.region ?? "—"}</td>
                      <td className="td-dim">{g.city ?? "—"}</td>
                      <td className="td-num">{fmtNum(g.visits)}</td>
                    </tr>
                  ))}
                </tbody>
              </TableWrap>
              <p className="attribution">Geo data: MaxMind GeoLite2. Visitor IPs never leave the server.</p>
            </>
          )}
        </Panel>
      </div>
    </>
  );
}
