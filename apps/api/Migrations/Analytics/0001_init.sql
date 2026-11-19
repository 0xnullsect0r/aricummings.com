-- Analytics events, in their own database so constant writes never contend with sign-ins.

CREATE TABLE visits(
    id            INTEGER PRIMARY KEY,
    visitor       TEXT NOT NULL,                     -- first-party cookie id
    at            INTEGER NOT NULL,
    path          TEXT NOT NULL,
    title         TEXT,
    referrer      TEXT,                              -- referring site, host only
    campaign      TEXT,
    ip            TEXT NOT NULL,                     -- full IP at collect; pruned after geo + retention
    ipv6_net      TEXT,                              -- /64 for IPv6
    user_agent    TEXT,
    browser       TEXT,
    os            TEXT,
    device        TEXT,
    screen        TEXT,
    language      TEXT,
    timezone      TEXT,
    country       TEXT,
    region        TEXT,
    city          TEXT,
    network       TEXT,
    entry_path    TEXT,
    exit_path     TEXT,
    session_id    TEXT,                              -- journey grouping
    active_ms     INTEGER,
    scroll_pct    INTEGER,
    bot           INTEGER NOT NULL DEFAULT 0,
    converted     INTEGER NOT NULL DEFAULT 0        -- contact submit on this journey
);
CREATE INDEX idx_visits_at ON visits(at);
CREATE INDEX idx_visits_session ON visits(session_id);

CREATE TABLE click_events(
    id          INTEGER PRIMARY KEY,
    visitor     TEXT NOT NULL,
    at          INTEGER NOT NULL,
    path        TEXT NOT NULL,
    label       TEXT NOT NULL,                       -- link text or element label
    target      TEXT,                                -- href or action
    x           INTEGER,
    y           INTEGER,
    section     TEXT
);
CREATE INDEX idx_clicks_at ON click_events(at);

CREATE TABLE bot_hits(
    id          INTEGER PRIMARY KEY,
    at          INTEGER NOT NULL,
    ip          TEXT NOT NULL,
    user_agent  TEXT,
    path        TEXT NOT NULL
);
CREATE INDEX idx_bot_at ON bot_hits(at);

CREATE TABLE daily_totals(
    day         TEXT PRIMARY KEY,                    -- '2026-11-19', site-local
    hits        INTEGER NOT NULL DEFAULT 0
);
