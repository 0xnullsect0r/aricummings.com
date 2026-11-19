/**
 * Sign-in: email + password, an optional 2FA step when the server says so, a
 * recovery-code fallback, and passkey sign-in. No credentials are kept anywhere
 * beyond the HttpOnly cookies the server sets.
 */

import { useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { encodeAssertion, toRequestOptions, useAuth } from "../auth";
import { Btn, Field, Input } from "../ui";

type Step = "password" | "2fa";

export function SignInScreen(): ReactElement {
  const auth = useAuth();
  const [step, setStep] = useState<Step>("password");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [useRecovery, setUseRecovery] = useState(false);
  const [pendingToken, setPendingToken] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const errText = (err: unknown): string => {
    if (err instanceof ApiError) return err.code !== "" && err.code !== "unknown" ? `${err.message} (${err.code})` : err.message;
    return "Sign-in failed.";
  };

  const submitPassword = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      const result = await auth.signIn(email, password);
      if (result.kind === "2fa") {
        setPendingToken(result.token);
        setStep("2fa");
      }
      // signed_in: the auth provider already flipped the app state.
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  };

  const submitCode = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      await api("POST", "/api/auth/2fa", { token: pendingToken, code });
      await auth.completeSignIn();
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  };

  const backToPassword = (): void => {
    setStep("password");
    setCode("");
    setUseRecovery(false);
    setError("");
    setPassword("");
    setPendingToken("");
  };

  const signInWithPasskey = async (): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      const options = await api<Record<string, unknown>>("POST", "/api/auth/passkey/options", {});
      const credential = await navigator.credentials.get({ publicKey: toRequestOptions(options) });
      if (credential === null) throw new ApiError(0, "No passkey was offered.", "passkey_cancelled");
      await api("POST", "/api/auth/passkey/verify", encodeAssertion(credential as PublicKeyCredential));
      await auth.completeSignIn();
    } catch (err) {
      if (err instanceof DOMException && err.name === "NotAllowedError") setError("Passkey sign-in was cancelled.");
      else setError(errText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="signin-wrap">
      <div className="signin-box">
        <div className="signin-head">
          <h1>ARI CUMMINGS — ADMIN</h1>
          <p>Staff sign-in. Invite only.</p>
        </div>
        <div className="signin-body">
          {step === "password" ? (
            <form onSubmit={(e) => void submitPassword(e)}>
              {error !== "" ? <p className="form-error" role="alert">{error}</p> : null}
              <Field label="EMAIL" htmlFor="signin-email">
                <Input
                  id="signin-email"
                  type="email"
                  name="email"
                  autoComplete="username"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              </Field>
              <Field label="PASSWORD" htmlFor="signin-password">
                <Input
                  id="signin-password"
                  type="password"
                  name="password"
                  autoComplete="current-password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </Field>
              <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
                SIGN IN
              </Btn>
              <div className="signin-alt">
                <Btn variant="ghost" disabled={busy} onClick={() => void signInWithPasskey()}>
                  USE A PASSKEY
                </Btn>
              </div>
            </form>
          ) : (
            <form onSubmit={(e) => void submitCode(e)}>
              {error !== "" ? <p className="form-error" role="alert">{error}</p> : null}
              <Field
                label={useRecovery ? "RECOVERY CODE" : "AUTHENTICATOR CODE"}
                hint={
                  useRecovery
                    ? "Enter one of the single-use recovery codes you saved."
                    : "6-digit code from your authenticator app."
                }
              >
                <Input
                  inputMode={useRecovery ? "text" : "numeric"}
                  autoComplete="one-time-code"
                  required
                  autoFocus
                  className="input-mono"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                />
              </Field>
              <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
                VERIFY
              </Btn>
              <div className="signin-alt">
                <Btn
                  variant="ghost"
                  disabled={busy}
                  onClick={() => {
                    setUseRecovery((v) => !v);
                    setCode("");
                  }}
                >
                  {useRecovery ? "USE AN AUTHENTICATOR CODE" : "USE A RECOVERY CODE"}
                </Btn>
                <Btn variant="ghost" disabled={busy} onClick={backToPassword}>
                  BACK
                </Btn>
              </div>
            </form>
          )}
        </div>
      </div>
    </div>
  );
}
