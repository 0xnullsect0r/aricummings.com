/**
 * Media library: GET/POST /api/admin/media (multipart upload with progress),
 * PATCH alt, DELETE (the API refuses ids still referenced by any draft,
 * published document or old revision — its message is shown verbatim).
 */

import { useRef, useState } from "react";
import type { DragEvent, ReactElement } from "react";
import { api, ApiError, upload } from "../api";
import { fmtBytes, fmtDate } from "../format";
import { Btn, ConfirmBtn, errorText, Input, Panel, ScreenLoader, useAsync } from "../ui";
import type { MediaItem } from "../types";

function pickWidth(widths: number[], target: number): number {
  const sorted = [...widths].sort((a, b) => a - b);
  for (const w of sorted) if (w >= target) return w;
  return sorted[sorted.length - 1] ?? target;
}

function MediaPicture({ item }: { item: MediaItem }): ReactElement {
  const avif = item.widths.map((w) => `/media/${item.id}/${String(w)}.avif ${String(w)}w`).join(", ");
  const webp = item.widths.map((w) => `/media/${item.id}/${String(w)}.webp ${String(w)}w`).join(", ");
  const fallback = pickWidth(item.widths, 800);
  return (
    <picture>
      <source type="image/avif" srcSet={avif} />
      <source type="image/webp" srcSet={webp} />
      <img
        src={`/media/${item.id}/${String(fallback)}.webp`}
        width={item.w}
        height={item.h}
        loading="lazy"
        decoding="async"
        alt={item.alt === "" ? "" : item.alt}
      />
    </picture>
  );
}

function MediaCell({ item, onChanged }: { item: MediaItem; onChanged: () => void }): ReactElement {
  const [alt, setAlt] = useState(item.alt);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const saveAlt = async (): Promise<void> => {
    setBusy(true);
    setError("");
    try {
      await api("PATCH", `/api/admin/media/${encodeURIComponent(item.id)}`, { alt });
      onChanged();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save the alt text.");
    } finally {
      setBusy(false);
    }
  };

  const remove = async (): Promise<void> => {
    setError("");
    try {
      await api("DELETE", `/api/admin/media/${encodeURIComponent(item.id)}`);
      onChanged();
    } catch (err) {
      // The in-use refusal lands here; show the API's reason verbatim.
      setError(err instanceof ApiError ? err.message : "Could not delete this file.");
    }
  };

  return (
    <div className="media-cell">
      <a href={`/media/${item.id}/${String(pickWidth(item.widths, 1200))}.webp`} target="_blank" rel="noreferrer noopener">
        <MediaPicture item={item} />
      </a>
      <div className="media-meta">
        {item.id} · {String(item.w)}×{String(item.h)} · {fmtBytes(item.bytes)}
        <br />
        {fmtDate(item.createdAt)}
      </div>
      <div className="media-pick">
        <Input
          type="text"
          aria-label={`ALT TEXT FOR ${item.id}`}
          placeholder="alt text"
          value={alt}
          onChange={(e) => setAlt(e.target.value)}
        />
        <Btn variant="ghost" small busy={busy} disabled={busy} onClick={() => void saveAlt()}>
          SAVE
        </Btn>
      </div>
      {item.alt === "" ? <p className="media-alt td-muted">No alt text.</p> : null}
      {error !== "" ? (
        <p className="field-error" role="alert">
          {error}
        </p>
      ) : null}
      <ConfirmBtn
        variant="danger"
        small
        question={`DELETE ${item.id}?`}
        confirmLabel="DELETE"
        onConfirm={() => remove()}
      >
        DELETE
      </ConfirmBtn>
    </div>
  );
}

export function MediaScreen(): ReactElement {
  const state = useAsync<MediaItem[]>(() => api<{ media: MediaItem[] }>("GET", "/api/admin/media").then(r => r.media), []);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const [progress, setProgress] = useState<number | null>(null);
  const [uploadError, setUploadError] = useState("");
  const [dragOver, setDragOver] = useState(false);

  const startUpload = (file: File): void => {
    setUploadError("");
    setProgress(0);
    upload("/api/admin/media", file, "file", (fraction) => setProgress(fraction))
      .then(() => {
        setProgress(null);
        state.reload();
      })
      .catch((err: unknown) => {
        setProgress(null);
        setUploadError(err instanceof ApiError ? `${err.message} (${err.code})` : "Upload failed.");
      });
  };

  const onFileChange = (e: React.ChangeEvent<HTMLInputElement>): void => {
    const file = e.target.files?.[0];
    if (file !== undefined) startUpload(file);
    e.target.value = "";
  };

  const onDrop = (e: DragEvent<HTMLDivElement>): void => {
    e.preventDefault();
    setDragOver(false);
    const file = e.dataTransfer.files[0];
    if (file !== undefined) startUpload(file);
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
        <h1>MEDIA</h1>
        <span className="screen-sub">{state.data.length} files</span>
      </div>

      <Panel label="UPLOAD">
        <div
          className={`block${dragOver ? " is-drag" : ""}`}
          style={{ borderStyle: "dashed", padding: 0 }}
          onDragOver={(e) => {
            e.preventDefault();
            setDragOver(true);
          }}
          onDragLeave={() => setDragOver(false)}
          onDrop={onDrop}
        >
          <div className="block-body" style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
            <input ref={fileRef} type="file" accept="image/jpeg,image/png,image/webp,image/avif,image/tiff" onChange={onFileChange} hidden />
            <Btn variant="primary" onClick={() => fileRef.current?.click()} disabled={progress !== null}>
              CHOOSE FILE
            </Btn>
            <span className="td-muted">or drop an image here — JPEG, PNG, WebP, AVIF (TIFF for seed photos), max 40 MB</span>
          </div>
          {progress !== null ? (
            <div className="block-body" style={{ paddingTop: 0 }}>
              <div
                className="upload-bar"
                role="progressbar"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={Math.round(progress * 100)}
              >
                <div className="upload-bar-fill" style={{ width: `${String(Math.round(progress * 100))}%` }} />
              </div>
            </div>
          ) : null}
        </div>
        {uploadError !== "" ? (
          <p className="field-error" role="alert">
            {uploadError}
          </p>
        ) : null}
      </Panel>

      <Panel label="LIBRARY" actions={<Btn variant="ghost" small onClick={state.reload}>REFRESH</Btn>}>
        {state.data.length === 0 ? (
          <p className="empty">No files yet. Upload the first one above.</p>
        ) : (
          <div className="media-grid">
            {state.data.map((item) => (
              <MediaCell key={item.id} item={item} onChanged={state.reload} />
            ))}
          </div>
        )}
      </Panel>
    </>
  );
}
