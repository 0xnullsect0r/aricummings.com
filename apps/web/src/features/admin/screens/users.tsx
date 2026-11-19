/**
 * Users (owner-only in practice): list, invite (one-time link shown exactly
 * once), deactivate/reactivate, permission editor, password-reset link.
 * GET|POST /api/admin/users, PATCH /api/admin/users/{id},
 * POST /api/admin/users/{id}/reset.
 */

import { useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { PERMISSIONS, useAuth } from "../auth";
import { fmtDate, fmtRelative } from "../format";
import {
  Btn,
  CheckRow,
  ConfirmBtn,
  errorText,
  Field,
  Input,
  Modal,
  Panel,
  Pill,
  ScreenLoader,
  TableWrap,
  useAsync,
} from "../ui";
import type { InviteResult, ResetResult, StaffUser } from "../types";

function OneTimeLink({ url, expiresAt, label }: { url: string; expiresAt: number; label: string }): ReactElement {
  return (
    <div className="one-time">
      <p>
        {label} — shown once, valid until {fmtDate(expiresAt)}. Copy it now; only a hash is stored.
      </p>
      <code>{url}</code>
    </div>
  );
}

function InviteForm({ onDone }: { onDone: () => void }): ReactElement {
  const [email, setEmail] = useState("");
  const [permissions, setPermissions] = useState<string[]>([]);
  const [result, setResult] = useState<InviteResult | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      const res = await api<InviteResult>("POST", "/api/admin/users", { email, permissions });
      setResult(res);
      setEmail("");
      setPermissions([]);
      onDone();
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (${err.code})` : "The invite failed.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel label="INVITE">
      {result !== null ? <OneTimeLink url={result.inviteUrl} expiresAt={result.inviteExpiresAt} label="INVITE LINK" /> : null}
      <form onSubmit={(e) => void submit(e)}>
        {error !== "" ? (
          <p className="form-error" role="alert">
            {error}
          </p>
        ) : null}
        <Field label="EMAIL">
          <Input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <fieldset style={{ border: 0, padding: 0, margin: "0 0 16px" }}>
          <span className="field-label">PERMISSIONS</span>
          {PERMISSIONS.map((p) => (
            <CheckRow
              key={p.key}
              label={p.key}
              description={p.description}
              checked={permissions.includes(p.key)}
              onChange={(next) =>
                setPermissions((list) => (next ? [...list, p.key] : list.filter((k) => k !== p.key)))
              }
            />
          ))}
        </fieldset>
        <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
          SEND INVITE
        </Btn>
      </form>
    </Panel>
  );
}

function EditUser({ user, onClose, onChanged }: { user: StaffUser; onClose: () => void; onChanged: () => void }): ReactElement {
  const { me } = useAuth();
  const [permissions, setPermissions] = useState<string[]>(user.permissions);
  const [name, setName] = useState(user.name);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [reset, setReset] = useState<ResetResult | null>(null);
  const [resetBusy, setResetBusy] = useState(false);
  const [stepUp, setStepUp] = useState<{ retry: () => Promise<void> } | null>(null);
  const [stepPassword, setStepPassword] = useState("");
  const [stepError, setStepError] = useState("");

  const save = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      await api("PATCH", `/api/admin/users/${encodeURIComponent(user.id)}`, { permissions, name });
      onChanged();
      onClose();
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (${err.code})` : "The save failed.");
    } finally {
      setBusy(false);
    }
  };

  const makeReset = async (): Promise<void> => {
    setResetBusy(true);
    setError("");
    try {
      const res = await api<ResetResult>("POST", `/api/admin/users/${encodeURIComponent(user.id)}/reset`);
      setReset(res);
      onChanged();
    } catch (err) {
      if (err instanceof ApiError && err.status === 403 && err.code === "step_up_required") {
        setStepPassword("");
        setStepError("");
        setStepUp({ retry: () => makeReset() });
      } else {
        setError(err instanceof ApiError ? `${err.message} (${err.code})` : "The reset link failed.");
      }
    } finally {
      setResetBusy(false);
    }
  };

  const runStepUp = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    if (stepUp === null) return;
    setResetBusy(true);
    setStepError("");
    try {
      await api("POST", "/api/auth/stepup", { password: stepPassword });
      const retry = stepUp.retry;
      setStepUp(null);
      await retry();
    } catch (err) {
      setStepError(err instanceof ApiError ? err.message : "Step-up failed.");
    } finally {
      setResetBusy(false);
    }
  };

  return (
    <Modal title={`EDIT ${user.email}`} onClose={onClose} wide>
      {reset !== null ? <OneTimeLink url={reset.resetUrl} expiresAt={reset.resetExpiresAt} label="PASSWORD RESET LINK" /> : null}
      {error !== "" ? (
        <p className="form-error" role="alert">
          {error}
        </p>
      ) : null}
      <form onSubmit={(e) => void save(e)}>
        <Field label="NAME">
          <Input type="text" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <fieldset style={{ border: 0, padding: 0, margin: "0 0 16px" }}>
          <span className="field-label">PERMISSIONS</span>
          {user.role === "owner" ? (
            <p className="td-dim" style={{ margin: 0 }}>
              Owners hold every permission implicitly.
            </p>
          ) : (
            PERMISSIONS.map((p) => (
              <CheckRow
                key={p.key}
                label={p.key}
                description={p.description}
                checked={permissions.includes(p.key)}
                onChange={(next) =>
                  setPermissions((list) => (next ? [...list, p.key] : list.filter((k) => k !== p.key)))
                }
              />
            ))
          )}
        </fieldset>
        <div className="row-actions">
          <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
            SAVE
          </Btn>
          <Btn type="button" variant="ghost" disabled={busy} onClick={onClose}>
            CANCEL
          </Btn>
        </div>
      </form>
      {me?.role === "owner" && user.id !== me?.id ? (
        <div style={{ marginTop: 16, paddingTop: 16, borderTop: "1px solid var(--line)" }}>
          <p className="td-muted" style={{ margin: "0 0 8px" }}>
            Password reset — the user gets a one-time link (1 hour) that invalidates older ones.
          </p>
          <Btn variant="ghost" busy={resetBusy} disabled={resetBusy} onClick={() => void makeReset()}>
            CREATE RESET LINK
          </Btn>
        </div>
      ) : null}

      {stepUp !== null ? (
        <Modal title="CONFIRM IT'S YOU" onClose={() => (resetBusy ? undefined : setStepUp(null))}>
          <form onSubmit={(e) => void runStepUp(e)}>
            <p className="td-dim" style={{ marginTop: 0 }}>
              Creating a reset link needs a fresh sign-in check. Enter your password to continue.
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
              <Btn type="submit" variant="primary" busy={resetBusy} disabled={resetBusy}>
                CONFIRM
              </Btn>
              <Btn type="button" variant="ghost" disabled={resetBusy} onClick={() => setStepUp(null)}>
                CANCEL
              </Btn>
            </div>
          </form>
        </Modal>
      ) : null}
    </Modal>
  );
}

export function UsersScreen(): ReactElement {
  const { me } = useAuth();
  const state = useAsync<StaffUser[]>(() => api<StaffUser[]>("GET", "/api/admin/users"), []);
  const [editing, setEditing] = useState<StaffUser | null>(null);
  const [actionError, setActionError] = useState("");

  const setActive = async (user: StaffUser, active: boolean): Promise<void> => {
    setActionError("");
    try {
      await api("PATCH", `/api/admin/users/${encodeURIComponent(user.id)}`, { active });
      state.reload();
    } catch (err) {
      setActionError(err instanceof ApiError ? `${err.message} (${err.code})` : "The update failed.");
    }
  };

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }

  const users = [...state.data].sort((a, b) => a.email.localeCompare(b.email));

  return (
    <>
      <div className="screen-head">
        <h1>USERS</h1>
        <span className="screen-sub">{users.length} staff accounts</span>
      </div>

      {actionError !== "" ? (
        <p className="form-error" role="alert">
          {actionError}
        </p>
      ) : null}

      <div className="stack">
        <Panel label="STAFF">
          <TableWrap>
            <thead>
              <tr>
                <th>Email</th>
                <th>Role</th>
                <th>State</th>
                <th>2FA</th>
                <th>Permissions</th>
                <th>Seen</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr key={user.id}>
                  <td className="td-mono td-wrap">
                    {user.email}
                    {me?.id === user.id ? <span className="td-muted"> (you)</span> : null}
                  </td>
                  <td>{user.role === "owner" ? <Pill tone="warn">OWNER</Pill> : <Pill tone="dim">STAFF</Pill>}</td>
                  <td>{user.active ? <Pill tone="ok">ACTIVE</Pill> : <Pill tone="danger">DEACTIVATED</Pill>}</td>
                  <td>{user.hasTotp ? <Pill tone="ok">ON</Pill> : <Pill tone="dim">OFF</Pill>}</td>
                  <td className="td-dim td-wrap">{user.role === "owner" ? "everything" : user.permissions.join(", ") || "none"}</td>
                  <td className="td-mono">{"—"}</td>
                  <td>
                    <div className="row-actions">
                      <Btn variant="ghost" small onClick={() => setEditing(user)}>
                        EDIT
                      </Btn>
                      {me?.id !== user.id ? (
                        <ConfirmBtn
                          variant={user.active ? "danger" : "default"}
                          small
                          question={
                            user.active
                              ? `DEACTIVATE ${user.email}? Their sessions stop working immediately.`
                              : `REACTIVATE ${user.email}?`
                          }
                          confirmLabel={user.active ? "DEACTIVATE" : "REACTIVATE"}
                          onConfirm={() => setActive(user, !user.active)}
                        >
                          {user.active ? "DEACTIVATE" : "REACTIVATE"}
                        </ConfirmBtn>
                      ) : null}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </TableWrap>
        </Panel>

        <InviteForm onDone={state.reload} />
      </div>

      {editing !== null ? <EditUser user={editing} onClose={() => setEditing(null)} onChanged={state.reload} /> : null}
    </>
  );
}
