/**
 * One-time-link screens: /invited?token=… (first sign-in, sets name + password)
 * and /reset?token=… (password reset). Both render for guests and signed-in
 * users alike — the links arrive by email and CLI output.
 */

import { useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { Btn, Field, Input } from "../ui";
import { Link } from "../router";

function tokenFromUrl(name: string): string {
  return new URLSearchParams(window.location.search).get(name) ?? "";
}

function OneTimeShell({ title, children }: { title: string; children: React.ReactNode }): ReactElement {
  return (
    <div className="ari-root">
      <div className="signin-wrap">
        <h1 className="signin-title">ARI CUMMINGS — ADMIN</h1>
        <p className="mono-caps signin-sub">{title}</p>
        {children}
      </div>
    </div>
  );
}

export function InvitedScreen(): ReactElement {
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [done, setDone] = useState(false);
  const token = tokenFromUrl("token");

  const submit = async (): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      await api("POST", "/api/auth/invite/accept", { token, name, password });
      setDone(true);
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (${err.code})` : "Could not accept the invite.");
    } finally {
      setBusy(false);
    }
  };

  if (token === "") {
    return (
      <OneTimeShell title="INVITE">
        <p className="td-dim">This link is missing its token. Ask for a new invite.</p>
        <p style={{ marginBottom: 0 }}>
          <Link to="">BACK TO SIGN IN</Link>
        </p>
      </OneTimeShell>
    );
  }

  return (
    <OneTimeShell title="ACCEPT INVITE">
      {done ? (
        <>
          <p className="td-dim">Password set. Sign in with your email and the new password.</p>
          <p style={{ marginBottom: 0 }}>
            <Link to="">GO TO SIGN IN</Link>
          </p>
        </>
      ) : (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault();
            if (!busy) void submit();
          }}
        >
          <Field label="YOUR NAME" htmlFor="invited-name">
            <Input id="invited-name" type="text" value={name} onChange={(e) => setName(e.target.value)} autoComplete="name" />
          </Field>
          <Field label="PASSWORD" htmlFor="invited-password">
            <Input id="invited-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          </Field>
          {error !== "" ? (
            <p className="form-error" role="alert">
              {error}
            </p>
          ) : null}
          <Btn type="submit" busy={busy} disabled={busy || name === "" || password === ""}>
            SET PASSWORD
          </Btn>
        </form>
      )}
    </OneTimeShell>
  );
}

export function ResetScreen(): ReactElement {
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [done, setDone] = useState(false);
  const token = tokenFromUrl("token");

  const submit = async (): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      await api("POST", "/api/auth/reset/confirm", { token, password });
      setDone(true);
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (${err.code})` : "Could not set the new password.");
    } finally {
      setBusy(false);
    }
  };

  if (token === "") {
    return (
      <OneTimeShell title="RESET">
        <p className="td-dim">This link is missing its token. Request a new reset.</p>
        <p style={{ marginBottom: 0 }}>
          <Link to="">BACK TO SIGN IN</Link>
        </p>
      </OneTimeShell>
    );
  }

  return (
    <OneTimeShell title="NEW PASSWORD">
      {done ? (
        <>
          <p className="td-dim">Password changed. Sign in with the new password.</p>
          <p style={{ marginBottom: 0 }}>
            <Link to="">GO TO SIGN IN</Link>
          </p>
        </>
      ) : (
        <form
          className="stack"
          onSubmit={(e) => {
            e.preventDefault();
            if (!busy) void submit();
          }}
        >
          <Field label="NEW PASSWORD" htmlFor="reset-password">
            <Input id="reset-password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          </Field>
          {error !== "" ? (
            <p className="form-error" role="alert">
              {error}
            </p>
          ) : null}
          <Btn type="submit" busy={busy} disabled={busy || password === ""}>
            SET NEW PASSWORD
          </Btn>
        </form>
      )}
    </OneTimeShell>
  );
}
