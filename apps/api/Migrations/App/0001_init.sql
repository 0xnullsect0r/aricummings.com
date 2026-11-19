-- Everything the site owns except analytics events. Timestamps are epoch ms.
-- Additive only: never rewrite or drop; the previous image must run on this file.

CREATE TABLE settings(
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    encrypted   INTEGER NOT NULL DEFAULT 0,
    updated_at  INTEGER NOT NULL,
    updated_by  INTEGER
);

CREATE TABLE users(
    id              INTEGER PRIMARY KEY,
    email           TEXT NOT NULL UNIQUE,
    name            TEXT NOT NULL,
    password_hash   TEXT,
    role            TEXT NOT NULL DEFAULT 'staff',      -- 'owner' | 'staff'
    permissions     TEXT NOT NULL DEFAULT '[]',          -- JSON array; owners hold all implicitly
    active          INTEGER NOT NULL DEFAULT 1,
    totp_secret     TEXT,                                -- sealed, context totp:<userId>
    totp_pending    INTEGER NOT NULL DEFAULT 1,
    totp_last_step  INTEGER,
    failed_signins  INTEGER NOT NULL DEFAULT 0,
    locked_until    INTEGER,
    created_at      INTEGER NOT NULL,
    step_up_at      INTEGER
);

CREATE TABLE sessions(
    id            TEXT PRIMARY KEY,
    user_id       INTEGER NOT NULL REFERENCES users(id),
    created_at    INTEGER NOT NULL,
    last_seen_at  INTEGER NOT NULL,
    expires_at    INTEGER NOT NULL,
    revoked_at    INTEGER,
    refresh_hash      TEXT NOT NULL,                      -- sha256 of current refresh secret
    refresh_rotated_at INTEGER NOT NULL DEFAULT 0,
    retired_hashes    TEXT NOT NULL DEFAULT '[]'          -- JSON array; reuse means theft: revoke
);

CREATE TABLE auth_links(                                   -- invites and password resets
    id          INTEGER PRIMARY KEY,
    user_id     INTEGER REFERENCES users(id),
    kind        TEXT NOT NULL,                             -- 'invite' | 'reset'
    email       TEXT NOT NULL,
    token_hash  BLOB NOT NULL UNIQUE,                      -- sha256; the link holds the token
    created_at  INTEGER NOT NULL,
    expires_at  INTEGER NOT NULL,
    used_at     INTEGER
);

CREATE TABLE passkey_credentials(
    id                  TEXT PRIMARY KEY,                  -- credential id, base64url
    user_id             INTEGER NOT NULL REFERENCES users(id),
    public_key          BLOB NOT NULL,
    sign_count          INTEGER NOT NULL DEFAULT 0,
    transports          TEXT,
    name                TEXT,
    created_at          INTEGER NOT NULL,
    last_used_at        INTEGER
);

CREATE TABLE passkey_challenges(
    id          TEXT PRIMARY KEY,
    user_id     INTEGER,
    challenge   BLOB NOT NULL,
    created_at  INTEGER NOT NULL,
    expires_at  INTEGER NOT NULL
);

CREATE TABLE recovery_codes(
    id          INTEGER PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    code_hash   BLOB NOT NULL UNIQUE,
    used_at     INTEGER,
    created_at  INTEGER NOT NULL
);

CREATE TABLE documents(
    key          TEXT PRIMARY KEY,                         -- 'site', 'page:home', 'work:<slug>'
    draft        TEXT NOT NULL DEFAULT '{}',
    published    TEXT,
    version      INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL,
    updated_by   INTEGER
);

CREATE TABLE document_revisions(
    id          INTEGER PRIMARY KEY,
    key         TEXT NOT NULL REFERENCES documents(key),
    version     INTEGER NOT NULL,
    content     TEXT NOT NULL,
    note        TEXT,
    author      INTEGER,
    published_at INTEGER NOT NULL
);
CREATE INDEX idx_revisions_key ON document_revisions(key, version DESC);

CREATE TABLE redirects(
    id       INTEGER PRIMARY KEY,
    from_path TEXT NOT NULL UNIQUE,
    to_path  TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    created_by INTEGER
);

CREATE TABLE media(
    id          TEXT PRIMARY KEY,                          -- 16 hex chars of the sha256
    original    TEXT NOT NULL,                             -- original filename for reference
    width       INTEGER NOT NULL,
    height      INTEGER NOT NULL,
    bytes       INTEGER NOT NULL,
    sha256      TEXT NOT NULL,
    alt         TEXT,
    created_at  INTEGER NOT NULL,
    created_by  INTEGER
);

CREATE TABLE messages(
    id          INTEGER PRIMARY KEY,
    name        TEXT NOT NULL,
    email       TEXT NOT NULL,                             -- a claim, never proof
    body        TEXT NOT NULL,
    ip          TEXT,
    user_agent  TEXT,
    created_at  INTEGER NOT NULL,
    read_at     INTEGER,
    archived_at INTEGER
);

CREATE TABLE outbox(
    id          INTEGER PRIMARY KEY,
    kind        TEXT NOT NULL,                             -- 'mail'
    payload     TEXT NOT NULL,                             -- JSON: to, subject, text, html, reply_to
    attempts    INTEGER NOT NULL DEFAULT 0,
    next_attempt_at INTEGER NOT NULL,
    last_error  TEXT,
    sent_at     INTEGER,
    created_at  INTEGER NOT NULL
);
CREATE INDEX idx_outbox_pending ON outbox(next_attempt_at) WHERE sent_at IS NULL;

CREATE TABLE github_cache(
    id          TEXT PRIMARY KEY,                          -- 'profile' | 'repos'
    fetched_at  INTEGER NOT NULL,
    data        TEXT NOT NULL                              -- JSON exactly as fetched
);

CREATE TABLE jobs_state(
    name        TEXT PRIMARY KEY,
    last_run_at INTEGER,
    last_ok_at  INTEGER,
    last_error  TEXT
);
