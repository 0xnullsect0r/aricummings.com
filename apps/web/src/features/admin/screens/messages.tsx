/**
 * Messages: GET /api/admin/messages, PATCH read/archived,
 * DELETE /api/admin/messages/{id}. manage permission gates archive and delete.
 */

import { useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { useAuth } from "../auth";
import { fmtRelative } from "../format";
import { Btn, ConfirmBtn, errorText, Panel, Pill, ScreenLoader, TableWrap, useAsync } from "../ui";
import type { Message } from "../types";

function MessageRow({ msg, canManage, onChanged }: { msg: Message; canManage: boolean; onChanged: () => void }): ReactElement {
  const [open, setOpen] = useState(false);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const patch = async (body: { read?: boolean; archived?: boolean }): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      await api("PATCH", `/api/admin/messages/${encodeURIComponent(msg.id)}`, body);
      onChanged();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "The update failed.");
    } finally {
      setBusy(false);
    }
  };

  const toggleOpen = (): void => {
    setOpen((v) => !v);
    if (!msg.read && !open) void patch({ read: true });
  };

  return (
    <tr>
      <td style={{ width: 28 }}>
        {msg.read ? <span className="td-muted" aria-label="READ">·</span> : <Pill tone="warn">NEW</Pill>}
      </td>
      <td>
        <button type="button" className="btn btn-ghost btn-small" style={{ textAlign: "left" }} onClick={toggleOpen} aria-expanded={open}>
          <span className="td-mono" style={{ fontWeight: msg.read ? 400 : 700 }}>
            {msg.name} &lt;{msg.email}&gt;
          </span>
        </button>
        {error !== "" ? (
          <p className="field-error" role="alert">
            {error}
          </p>
        ) : null}
        {open ? (
          <div style={{ padding: "8px 0" }}>
            <p className="td-muted" style={{ margin: "0 0 6px" }}>
              {fmtRelative(msg.createdAt)}
            </p>
            <p className="message-body">{msg.body}</p>
            <a href={`mailto:${msg.email}`}>reply by email</a>
          </div>
        ) : null}
      </td>
      <td className="td-mono">{fmtRelative(msg.createdAt)}</td>
      <td>
        <div className="row-actions">
          {msg.read ? (
            <Btn variant="ghost" small busy={busy} disabled={busy || !canManage} onClick={() => void patch({ read: false })}>
              MARK UNREAD
            </Btn>
          ) : (
            <Btn variant="ghost" small busy={busy} disabled={busy} onClick={() => void patch({ read: true })}>
              MARK READ
            </Btn>
          )}
          {canManage ? (
            <>
              <Btn variant="ghost" small busy={busy} disabled={busy} onClick={() => void patch({ archived: !msg.archived })}>
                {msg.archived ? "UNARCHIVE" : "ARCHIVE"}
              </Btn>
              <ConfirmBtn
                variant="danger"
                small
                question={`DELETE THE MESSAGE FROM ${msg.name}?`}
                confirmLabel="DELETE"
                disabled={busy}
                onConfirm={async () => {
                  await api("DELETE", `/api/admin/messages/${encodeURIComponent(msg.id)}`);
                  onChanged();
                }}
              >
                DELETE
              </ConfirmBtn>
            </>
          ) : null}
        </div>
      </td>
    </tr>
  );
}

export function MessagesScreen(): ReactElement {
  const { can } = useAuth();
  const state = useAsync<Message[]>(() => api<{ items: Message[] }>("GET", "/api/admin/messages").then(r => r.items), []);
  const [box, setBox] = useState<"inbox" | "archived">("inbox");

  if (state.data === null) {
    return (
      <ScreenLoader error={state.error !== null ? errorText(state.error) : null} loading={state.loading} onRetry={state.reload}>
        <span />
      </ScreenLoader>
    );
  }

  const canManage = can("messages.manage");
  const rows = state.data.filter((m) => (box === "archived" ? m.archived : !m.archived));
  const unread = state.data.filter((m) => !m.read && !m.archived).length;

  return (
    <>
      <div className="screen-head">
        <h1>MESSAGES</h1>
        <span className="screen-sub">
          {unread === 0 ? "nothing unread" : `${String(unread)} unread`}
        </span>
      </div>

      <div className="filter-row" role="group" aria-label="FOLDER">
        <div className="segmented">
          <button type="button" aria-pressed={box === "inbox"} onClick={() => setBox("inbox")}>
            INBOX ({String(state.data.filter((m) => !m.archived).length)})
          </button>
          <button type="button" aria-pressed={box === "archived"} onClick={() => setBox("archived")}>
            ARCHIVED ({String(state.data.filter((m) => m.archived).length)})
          </button>
        </div>
        <Btn variant="ghost" onClick={state.reload}>
          REFRESH
        </Btn>
      </div>

      <Panel label={box === "inbox" ? "INBOX" : "ARCHIVED"}>
        {rows.length === 0 ? (
          <p className="empty">{box === "inbox" ? "The inbox is empty." : "Nothing archived."}</p>
        ) : (
          <TableWrap>
            <thead>
              <tr>
                <th />
                <th>From</th>
                <th>Received</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map((msg) => (
                <MessageRow key={msg.id} msg={msg} canManage={canManage} onChanged={state.reload} />
              ))}
            </tbody>
          </TableWrap>
        )}
      </Panel>
    </>
  );
}
