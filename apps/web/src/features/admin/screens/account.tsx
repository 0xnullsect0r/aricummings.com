/**
 * Account: own password change, TOTP setup (the QR arrives as a plain SVG from
 * the API and is rendered directly), recovery codes (shown once, downloadable),
 * passkey register/list/delete, active sessions with revoke.
 */

import { useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { encodeAttestation, toCreationOptions, useAuth } from "../auth";
import { fmtDate, fmtRelative } from "../format";
import {
  Btn,
  ConfirmBtn,
  errorText,
  Field,
  Input,
  Panel,
  Pill,
  TableWrap,
  useAsync,
} from "../ui";
import type { PasskeyRecord, RecoveryCodes, SessionRecord, TotpStart } from "../types";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.code !== "" && err.code !== "unknown" ? `${err.message} (${err.code})` : err.message;
  if (err instanceof Error) return err.message;
  return "The request failed.";
}

// ---------------------------------------------------------------- password

function PasswordPanel(): ReactElement {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await api("PUT", "/api/auth/account/password", { current, next });
      setCurrent("");
      setNext("");
      setNotice("Password changed.");
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel label="PASSWORD">
      <form onSubmit={(e) => void submit(e)}>
        {error !== "" ? (
          <p className="form-error" role="alert">
            {error}
          </p>
        ) : null}
        {notice !== "" ? (
          <p className="form-ok" role="status">
            {notice}
          </p>
        ) : null}
        <div className="form-row">
          <Field label="CURRENT PASSWORD">
            <Input type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
          <Field label="NEW PASSWORD" hint="12–128 characters. Not your email's local part, not a common word.">
            <Input type="password" autoComplete="new-password" required minLength={12} maxLength={128} value={next} onChange={(e) => setNext(e.target.value)} />
          </Field>
        </div>
        <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
          CHANGE PASSWORD
        </Btn>
      </form>
    </Panel>
  );
}

// ---------------------------------------------------------------- totp + recovery

function TotpPanel(): ReactElement {
  const [start, setStart] = useState<TotpStart | null>(null);
  const [code, setCode] = useState("");
  const [codes, setCodes] = useState<RecoveryCodes | null>(null);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState("");

  const begin = async (): Promise<void> => {
    setBusy("start");
    setError("");
    setNotice("");
    try {
      const res = await api<TotpStart>("POST", "/api/auth/account/totp/start");
      setStart(res);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy("");
    }
  };

  const confirm = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy("confirm");
    setError("");
    try {
      await api("POST", "/api/auth/account/totp/confirm", { code });
      setStart(null);
      setCode("");
      setNotice("Two-factor auth is on.");
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy("");
    }
  };

  const showRecovery = async (): Promise<void> => {
    setBusy("recovery");
    setError("");
    try {
      const res = await api<RecoveryCodes>("GET", "/api/auth/account/totp/recovery");
      setCodes(res);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy("");
    }
  };

  const download = (): void => {
    if (codes === null) return;
    const text = [
      "aricummings.com admin — recovery codes",
      "Each code works once. Keep this file somewhere safe.",
      "",
      ...codes.codes,
      "",
    ].join("\n");
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = "ari-admin-recovery-codes.txt";
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <Panel label="TWO-FACTOR AUTH">
      {error !== "" ? (
        <p className="form-error" role="alert">
          {error}
        </p>
      ) : null}
      {notice !== "" ? (
        <p className="form-ok" role="status">
          {notice}
        </p>
      ) : null}

      {start !== null ? (
        <form onSubmit={(e) => void confirm(e)}>
          <p className="td-dim" style={{ marginTop: 0 }}>
            Scan with your authenticator app, then enter a code to prove it works. The secret stays pending until then.
          </p>
          <div className="qr-box" role="img" aria-label="TOTP QR CODE">
            <span dangerouslySetInnerHTML={{ __html: start.qr }} />
          </div>
          <p className="td-mono" style={{ overflowWrap: "anywhere" }}>
            {start.otpauth}
          </p>
          <Field label="CODE FROM YOUR APP">
            <Input inputMode="numeric" className="input-mono" autoComplete="one-time-code" required autoFocus value={code} onChange={(e) => setCode(e.target.value)} />
          </Field>
          <div className="row-actions">
            <Btn type="submit" variant="primary" busy={busy === "confirm"} disabled={busy !== ""}>
              CONFIRM
            </Btn>
            <Btn variant="ghost" disabled={busy !== ""} onClick={() => setStart(null)}>
              CANCEL
            </Btn>
          </div>
        </form>
      ) : (
        <div className="row-actions">
          <Btn variant="primary" busy={busy === "start"} disabled={busy !== ""} onClick={() => void begin()}>
            SET UP AUTHENTICATOR
          </Btn>
          <ConfirmBtn
            variant="ghost"
            question="SHOW RECOVERY CODES?"
            confirmLabel="SHOW"
            busy={busy === "recovery"}
            disabled={busy !== ""}
            onConfirm={() => showRecovery()}
          >
            SHOW RECOVERY CODES
          </ConfirmBtn>
        </div>
      )}

      {codes !== null ? (
        <div style={{ marginTop: 16 }}>
          <p className="td-dim" style={{ marginTop: 0 }}>
            Each code works once. Shown now — download them; the server stores only hashes.
          </p>
          <ul className="code-list">
            {codes.codes.map((c) => (
              <li key={c}>{c}</li>
            ))}
          </ul>
          <Btn variant="ghost" onClick={download}>
            DOWNLOAD .TXT
          </Btn>
        </div>
      ) : null}
    </Panel>
  );
}

// ---------------------------------------------------------------- passkeys

function PasskeyPanel(): ReactElement {
  const state = useAsync<PasskeyRecord[]>(
    async () => (await api<{ items: PasskeyRecord[] }>("GET", "/api/auth/account/passkeys")).items,
    [],
  );
  const [name, setName] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const register = async (): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      const options = await api<Record<string, unknown>>("POST", "/api/auth/account/passkeys/register", { name });
      const credential = await navigator.credentials.create({ publicKey: toCreationOptions(options) });
      if (credential === null) throw new ApiError(0, "No passkey was created.", "passkey_cancelled");
      await api("POST", "/api/auth/account/passkeys/register/verify", { name, credential: encodeAttestation(credential as PublicKeyCredential) });
      setName("");
      state.reload();
    } catch (err) {
      if (err instanceof DOMException && err.name === "NotAllowedError") setError("Passkey registration was cancelled.");
      else setError(errText(err));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id: string): Promise<void> => {
    setError("");
    try {
      await api("DELETE", `/api/auth/account/passkeys/${String(id)}`);
      state.reload();
    } catch (err) {
      setError(errText(err));
    }
  };

  return (
    <Panel
      label="PASSKEYS"
      actions={
        <Btn variant="ghost" small onClick={state.reload}>
          REFRESH
        </Btn>
      }
    >
      {error !== "" ? (
        <p className="form-error" role="alert">
          {error}
        </p>
      ) : null}
      {state.error !== null ? (
        <p className="form-error" role="alert">
          {errorText(state.error)}
        </p>
      ) : null}
      <div className="media-pick" style={{ marginBottom: 16 }}>
        <Input type="text" placeholder="name (e.g. YubiKey 5C, iPhone)" value={name} onChange={(e) => setName(e.target.value)} aria-label="PASSKEY NAME" />
        <Btn variant="primary" busy={busy} disabled={busy || name.trim() === ""} onClick={() => void register()}>
          REGISTER
        </Btn>
      </div>
      {state.loading ? (
        <div className="state-block" role="status">
          <span className="spinner" aria-hidden="true" />
          <span className="mono-caps">LOADING</span>
        </div>
      ) : (state.data ?? []).length === 0 ? (
        <p className="empty">No passkeys registered.</p>
      ) : (
        <TableWrap>
          <thead>
            <tr>
              <th>Name</th>
              <th>Created</th>
              <th>Last used</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(state.data ?? []).map((pk) => (
              <tr key={pk.id}>
                <td className="td-mono td-wrap">{pk.name}</td>
                <td className="td-mono">{fmtDate(pk.createdAt)}</td>
                <td className="td-mono">{fmtRelative(pk.lastUsedAt)}</td>
                <td>
                  <ConfirmBtn
                    variant="danger"
                    small
                    question={`DELETE PASSKEY "${pk.name}"?`}
                    confirmLabel="DELETE"
                    onConfirm={() => remove(String(pk.id))}
                  >
                    DELETE
                  </ConfirmBtn>
                </td>
              </tr>
            ))}
          </tbody>
        </TableWrap>
      )}
    </Panel>
  );
}

// ---------------------------------------------------------------- sessions

function SessionsPanel(): ReactElement {
  const state = useAsync<{ sessions: SessionRecord[] }>(() => api<{ sessions: SessionRecord[] }>("GET", "/api/auth/account/sessions"), []);
  const [error, setError] = useState("");

  const revoke = async (id: string): Promise<void> => {
    setError("");
    try {
      await api("DELETE", `/api/auth/account/sessions/${encodeURIComponent(id)}`);
      state.reload();
    } catch (err) {
      setError(errText(err));
    }
  };

  return (
    <Panel
      label="ACTIVE SESSIONS"
      actions={
        <Btn variant="ghost" small onClick={state.reload}>
          REFRESH
        </Btn>
      }
    >
      {error !== "" ? (
        <p className="form-error" role="alert">
          {error}
        </p>
      ) : null}
      {state.error !== null ? (
        <p className="form-error" role="alert">
          {errorText(state.error)}
        </p>
      ) : state.loading ? (
        <div className="state-block" role="status">
          <span className="spinner" aria-hidden="true" />
          <span className="mono-caps">LOADING</span>
        </div>
      ) : (state.data?.sessions ?? []).length === 0 ? (
        <p className="empty">No sessions listed.</p>
      ) : (
        <TableWrap>
          <thead>
            <tr>
              <th>Started</th>
              <th>Last seen</th>
              <th>IP</th>
              <th>Device</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(state.data?.sessions ?? []).map((s) => (
              <tr key={s.id}>
                <td className="td-mono">{fmtDate(s.createdAt)}</td>
                <td className="td-mono">{fmtRelative(s.lastSeenAt)}</td>
                <td className="td-mono">{s.ip ?? "—"}</td>
                <td className="td-dim td-wrap">
                  {s.userAgent ?? "—"} {s.current ? <Pill tone="warn">THIS DEVICE</Pill> : null}
                </td>
                <td>
                  <ConfirmBtn
                    variant="danger"
                    small
                    question={s.current ? "REVOKE THIS SESSION? You will be signed out here." : "REVOKE THIS SESSION?"}
                    confirmLabel="REVOKE"
                    onConfirm={() => revoke(s.id)}
                  >
                    REVOKE
                  </ConfirmBtn>
                </td>
              </tr>
            ))}
          </tbody>
        </TableWrap>
      )}
    </Panel>
  );
}

// ---------------------------------------------------------------- screen

export function AccountScreen(): ReactElement {
  const { me } = useAuth();
  return (
    <>
      <div className="screen-head">
        <h1>ACCOUNT</h1>
        <span className="screen-sub">{me?.email ?? ""}</span>
      </div>
      <div className="stack">
        <PasswordPanel />
        <TotpPanel />
        <PasskeyPanel />
        <SessionsPanel />
      </div>
    </>
  );
}
