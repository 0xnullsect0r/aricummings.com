-- The audit log. Created here because the content and media write paths record into it
-- before the audit feature exists; the audit feature reads it, never recreates it.
-- Timestamps are epoch ms; 'at' is a plain column, because two entries can share a
-- millisecond and the rowid keeps inserts cheap and ordered.

CREATE TABLE audit_log(
    id      INTEGER PRIMARY KEY,
    at      INTEGER NOT NULL,
    actor   INTEGER,
    action  TEXT NOT NULL,
    target  TEXT,
    detail  TEXT,
    ip      TEXT
);
CREATE INDEX idx_audit_at ON audit_log(at DESC);
