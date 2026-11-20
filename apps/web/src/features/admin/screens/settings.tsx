/**
 * Settings: GET /api/admin/settings, PUT /api/admin/settings/{key}.
 *
 * hasPassword pattern: a saved secret is never echoed — only the fact that one
 * exists. Typing a new secret requires re-typing it whenever another field in
 * the same group (host/user) changed, so a stored credential can never be
 * silently pointed at a different server. Owner-only groups (mail, keys) are
 * hidden for non-owners; the server enforces both owner and step-up anyway.
 */

import { useMemo, useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { useAuth } from "../auth";
import { fmtRelative } from "../format";
import { Btn, errorText, Field, Input, Modal, Panel, Pill, ScreenLoader, useAsync } from "../ui";
import type { SettingRow } from "../types";

const OWNER_GROUP_PREFIXES = new Set(["smtp", "mail", "github"]);
const SECRET_HINT = "•••••••• saved — type above to replace";

function groupOf(key: string): string {
  const dot = key.indexOf(".");
  return dot === -1 ? key : key.slice(0, dot);
}

function isSecretRow(row: SettingRow): boolean {
  return row.hasValue || /password|secret|token|key/i.test(row.key);
}

interface Draft {
  values: Record<string, string>;
  confirm: Record<string, string>;
}

function GroupEditor({ rows, onSaved }: { rows: SettingRow[]; onSaved: () => void }): ReactElement {
  const group = groupOf(rows[0]?.key ?? "");
  const [draft, setDraft] = useState<Draft>({ values: {}, confirm: {} });
  const [error, setError] = useState("");
  const [confirmError, setConfirmError] = useState("");
  const [busy, setBusy] = useState(false);
  const [stepUp, setStepUp] = useState<{ retry: () => Promise<void> } | null>(null);
  const [stepPassword, setStepPassword] = useState("");
  const [stepError, setStepError] = useState("");

  const secretKeys = useMemo(() => rows.filter(isSecretRow).map((r) => r.key), [rows]);

  const dirty = (row: SettingRow): boolean => draft.values[row.key] !== undefined && draft.values[row.key] !== (row.value ?? "");
  const groupDirtyNonSecret = rows.some((r) => !isSecretRow(r) && dirty(r));

  const setValue = (key: string, value: string): void => {
    setDraft((d) => ({ ...d, values: { ...d.values, [key]: value } }));
  };
  const setConfirm = (key: string, value: string): void => {
    setDraft((d) => ({ ...d, confirm: { ...d.confirm, [key]: value } }));
  };

  const performSaves = async (saves: { key: string; value: string }[]): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      for (const save of saves) {
        await api("PUT", `/api/admin/settings/${encodeURIComponent(save.key)}`, { value: save.value });
      }
      setDraft({ values: {}, confirm: {} });
      onSaved();
    } catch (err) {
      if (err instanceof ApiError && err.status === 403 && err.code === "step_up_required") {
        setStepUp({ retry: () => performSaves(saves) });
        setStepPassword("");
        setStepError("");
      } else {
        setError(err instanceof ApiError ? `${err.message} (${err.code})` : "The save failed.");
      }
    } finally {
      setBusy(false);
    }
  };

  const save = async (): Promise<void> => {
    setConfirmError("");
    const saves: { key: string; value: string }[] = [];
    for (const row of rows) {
      const secret = isSecretRow(row);
      if (secret) {
        const typed = draft.values[row.key] ?? "";
        if (typed !== "") saves.push({ key: row.key, value: typed });
      } else if (dirty(row)) {
        saves.push({ key: row.key, value: draft.values[row.key] ?? "" });
      }
    }
    if (saves.length === 0) return;
    // The stored-secret rule: retyping is required when the credential's
    // destination (host/user) in this group changed at the same time.
    if (groupDirtyNonSecret) {
      for (const key of secretKeys) {
        const typed = draft.values[key] ?? "";
        if (typed !== "" && (draft.confirm[key] ?? "") !== typed) {
          setConfirmError("Re-enter the new value: it can be sent to a different server now that host or user changed.");
          return;
        }
      }
    }
    await performSaves(saves);
  };

  const runStepUp = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    if (stepUp === null) return;
    setBusy(true);
    setStepError("");
    try {
      await api("POST", "/api/auth/stepup", { password: stepPassword });
      const retry = stepUp.retry;
      setStepUp(null);
      await retry();
    } catch (err) {
      setStepError(err instanceof ApiError ? err.message : "Step-up failed.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel
      label={group.toUpperCase()}
      actions={rows.some((r) => isSecretRow(r) && r.hasValue) ? <Pill tone="dim">SECRET SAVED</Pill> : undefined}
    >
      {error !== "" ? (
        <p className="form-error" role="alert">
          {error}
        </p>
      ) : null}
      {confirmError !== "" ? (
        <p className="form-error" role="alert">
          {confirmError}
        </p>
      ) : null}
      <div className="form-row">
        {rows.map((row) => {
          const secret = isSecretRow(row);
          const typed = draft.values[row.key] ?? "";
          return (
            <div key={row.key} style={{ marginBottom: 16 }}>
              <Field
                label={row.key}
                hint={secret && row.hasValue && typed === "" ? SECRET_HINT : `updated ${fmtRelative(row.updatedAt)} by ${row.updatedBy}`}
              >
                <Input
                  type={secret ? "password" : "text"}
                  className={secret ? undefined : "input-mono"}
                  autoComplete="off"
                  value={secret ? typed : row.value ?? ""}
                  placeholder={secret ? "new value" : ""}
                  onChange={(e) => setValue(row.key, e.target.value)}
                />
              </Field>
              {secret && groupDirtyNonSecret && typed !== "" ? (
                <Field label={`${row.key} — AGAIN`} hint="Required because this group's destination changed.">
                  <Input
                    type="password"
                    autoComplete="off"
                    value={draft.confirm[row.key] ?? ""}
                    onChange={(e) => setConfirm(row.key, e.target.value)}
                  />
                </Field>
              ) : null}
            </div>
          );
        })}
      </div>
      <Btn variant="primary" busy={busy} disabled={busy} onClick={() => void save()}>
        SAVE {group.toUpperCase()}
      </Btn>

      {stepUp !== null ? (
        <Modal title="CONFIRM IT'S YOU" onClose={() => (busy ? undefined : setStepUp(null))}>
          <form onSubmit={(e) => void runStepUp(e)}>
            <p className="td-dim" style={{ marginTop: 0 }}>
              Changing these settings needs a fresh sign-in check. Enter your password to continue.
            </p>
            {stepError !== "" ? (
              <p className="form-error" role="alert">
                {stepError}
              </p>
            ) : null}
            <Field label="PASSWORD">
              <Input type="password" autoComplete="current-password" autoFocus value={stepPassword} onChange={(e) => setStepPassword(e.target.value)} />
            </Field>
            <div className="confirm-row">
              <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
                CONFIRM
              </Btn>
              <Btn type="button" variant="ghost" disabled={busy} onClick={() => setStepUp(null)}>
                CANCEL
              </Btn>
            </div>
          </form>
        </Modal>
      ) : null}
    </Panel>
  );
}

export function SettingsScreen(): ReactElement {
  const { me } = useAuth();
  const state = useAsync<SettingRow[]>(() => api<{ items: SettingRow[] }>("GET", "/api/admin/settings").then(r => r.items), []);

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }

  const owner = me?.role === "owner";
  const groups = new Map<string, SettingRow[]>();
  for (const row of state.data) {
    const group = groupOf(row.key);
    if (!owner && OWNER_GROUP_PREFIXES.has(group)) continue;
    const list = groups.get(group) ?? [];
    list.push(row);
    groups.set(group, list);
  }

  return (
    <>
      <div className="screen-head">
        <h1>SETTINGS</h1>
        <span className="screen-sub">saved values never echo for secrets</span>
      </div>
      <div className="stack">
        {groups.size === 0 ? (
          <p className="empty">No settings rows visible to you.</p>
        ) : (
          [...groups.entries()].map(([group, rows]) => <GroupEditor key={group} rows={rows} onSaved={state.reload} />)
        )}
        {!owner ? (
          <p className="td-muted" style={{ margin: 0 }}>
            Mail and integration-credential settings are owner-only.
          </p>
        ) : null}
      </div>
    </>
  );
}
