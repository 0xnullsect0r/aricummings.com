-- Ingest accounting and weekly-report idempotency. Additive only.

CREATE TABLE ingest_stats(
    day      TEXT PRIMARY KEY,                    -- '2026-11-19', site-local
    received INTEGER NOT NULL DEFAULT 0,          -- events accepted into the buffer
    dropped  INTEGER NOT NULL DEFAULT 0,          -- overflow, disk guard, lost batches
    bots     INTEGER NOT NULL DEFAULT 0           -- bot beacons logged to bot_hits
);

CREATE TABLE weekly_reports(
    period  TEXT PRIMARY KEY,                     -- ISO week the report covers, '2026-W46'
    sent_at INTEGER NOT NULL
);

CREATE INDEX idx_visits_visitor ON visits(visitor, at);
CREATE INDEX idx_clicks_path ON click_events(path, at);

-- Distinguishes plain clicks (click maps) from outbound link clicks and contact-form
-- conversions, all of which are always counted as conversions.
ALTER TABLE click_events ADD COLUMN kind TEXT NOT NULL DEFAULT 'click';   -- 'click' | 'outbound' | 'conversion'
-- Journey id the click belongs to, for funnel steps at session granularity.
ALTER TABLE click_events ADD COLUMN session TEXT;
