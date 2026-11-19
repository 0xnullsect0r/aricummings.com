-- 0001 declared visits.ip NOT NULL, which contradicts what the column itself documents:
-- "full IP at collect; pruned after geo + retention". Retention needs to null it.
-- One-time rebuild to relax the constraint; every writer always sets ip, so the
-- running image is unaffected by the relaxed column.

CREATE TABLE visits_v3(
    id            INTEGER PRIMARY KEY,
    visitor       TEXT NOT NULL,
    at            INTEGER NOT NULL,
    path          TEXT NOT NULL,
    title         TEXT,
    referrer      TEXT,
    campaign      TEXT,
    ip            TEXT,                              -- nullable: pruned after retention
    ipv6_net      TEXT,
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
    session_id    TEXT,
    active_ms     INTEGER,
    scroll_pct    INTEGER,
    bot           INTEGER NOT NULL DEFAULT 0,
    converted     INTEGER NOT NULL DEFAULT 0
);
INSERT INTO visits_v3(id, visitor, at, path, title, referrer, campaign, ip, ipv6_net, user_agent,
                      browser, os, device, screen, language, timezone, country, region, city,
                      network, entry_path, exit_path, session_id, active_ms, scroll_pct, bot, converted)
SELECT id, visitor, at, path, title, referrer, campaign, ip, ipv6_net, user_agent,
       browser, os, device, screen, language, timezone, country, region, city,
       network, entry_path, exit_path, session_id, active_ms, scroll_pct, bot, converted
FROM visits;
DROP TABLE visits;
ALTER TABLE visits_v3 RENAME TO visits;

-- The indexes above died with the old table; same names, same shapes.
CREATE INDEX idx_visits_at ON visits(at);
CREATE INDEX idx_visits_session ON visits(session_id);
CREATE INDEX idx_visits_visitor ON visits(visitor, at);
