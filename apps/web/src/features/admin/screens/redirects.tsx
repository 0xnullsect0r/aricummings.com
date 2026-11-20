/** Redirects: GET/POST /api/admin/redirects, DELETE /api/admin/redirects/{id}. */

import { useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { Btn, ConfirmBtn, errorText, Field, Input, Panel, ScreenLoader, TableWrap, useAsync } from "../ui";

interface RedirectRow {
  id: string;
  from: string;
  to: string;
}

export function RedirectsScreen(): ReactElement {
  const state = useAsync<RedirectRow[]>(() => api<{ redirects: RedirectRow[] }>("GET", "/api/admin/redirects").then(r => r.redirects), []);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const add = async (e: React.FormEvent): Promise<void> => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      await api("POST", "/api/admin/redirects", { from, to });
      setFrom("");
      setTo("");
      state.reload();
    } catch (err) {
      setError(err instanceof ApiError ? `${err.message} (${err.code})` : "Could not add the redirect.");
    } finally {
      setBusy(false);
    }
  };

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }

  return (
    <>
      <div className="screen-head">
        <h1>REDIRECTS</h1>
        <span className="screen-sub">{state.data.length} rules · 301 on GET page paths</span>
      </div>

      <Panel label="ADD">
        <form onSubmit={(e) => void add(e)}>
          {error !== "" ? (
            <p className="form-error" role="alert">
              {error}
            </p>
          ) : null}
          <div className="form-row">
            <Field label="FROM PATH" hint="e.g. /old-page">
              <Input type="text" className="input-mono" required value={from} onChange={(e) => setFrom(e.target.value)} />
            </Field>
            <Field label="TO PATH" hint="e.g. /work/enclave">
              <Input type="text" className="input-mono" required value={to} onChange={(e) => setTo(e.target.value)} />
            </Field>
          </div>
          <Btn type="submit" variant="primary" busy={busy} disabled={busy}>
            ADD REDIRECT
          </Btn>
        </form>
      </Panel>

      <Panel label="RULES">
        {state.data.length === 0 ? (
          <p className="empty">No redirects.</p>
        ) : (
          <TableWrap>
            <thead>
              <tr>
                <th>From</th>
                <th>To</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {state.data.map((row) => (
                <tr key={row.id}>
                  <td className="td-mono td-wrap">{row.from}</td>
                  <td className="td-mono td-wrap">{row.to}</td>
                  <td>
                    <ConfirmBtn
                      variant="danger"
                      small
                      question={`DELETE REDIRECT ${row.from} → ${row.to}?`}
                      confirmLabel="DELETE"
                      onConfirm={() =>
                        api("DELETE", `/api/admin/redirects/${encodeURIComponent(row.id)}`).then(state.reload)
                      }
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
    </>
  );
}
