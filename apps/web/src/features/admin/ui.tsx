/**
 * Hand-rolled components in the design language: dark tokens, mono labels,
 * sharp corners, 1px lines, 44px targets, 2px accent focus. No UI library.
 */

import { useCallback, useEffect, useRef, useState } from "react";
import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactElement, ReactNode, SelectHTMLAttributes, TextareaHTMLAttributes } from "react";
import { ApiError } from "./api";

// ---------------------------------------------------------------- async hook

export interface AsyncState<T> {
  data: T | null;
  error: { message: string; code: string } | null;
  loading: boolean;
  reload: () => void;
}

export function useAsync<T>(fn: () => Promise<T>, deps: unknown[]): AsyncState<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<{ message: string; code: string } | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const fnRef = useRef(fn);
  fnRef.current = fn;

  useEffect(() => {
    let alive = true;
    setLoading(true);
    fnRef
      .current()
      .then((value) => {
        if (!alive) return;
        setData(value);
        setError(null);
      })
      .catch((err: unknown) => {
        if (!alive) return;
        if (err instanceof ApiError) setError({ message: err.message, code: err.code });
        else if (err instanceof Error && err.name === "AbortError") return;
        else setError({ message: err instanceof Error ? err.message : "Something failed.", code: "unknown" });
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);

  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { data, error, loading, reload };
}

export function errorText(err: { message: string; code: string } | ApiError | null | undefined): string {
  if (!err) return "";
  const code = "code" in err ? err.code : "";
  return code !== "" && code !== "unknown" ? `${err.message} (${code})` : err.message;
}

// ---------------------------------------------------------------- panels

export function Panel({ label, actions, children, id }: { label: string; actions?: ReactNode; children: ReactNode; id?: string }): ReactElement {
  return (
    <section className="panel" id={id}>
      <div className="panel-head">
        <h2>{label}</h2>
        {actions !== undefined && actions !== null ? <div className="panel-actions">{actions}</div> : null}
      </div>
      <div className="panel-body">{children}</div>
    </section>
  );
}

// ---------------------------------------------------------------- buttons

type BtnVariant = "default" | "primary" | "danger" | "ghost";

export function Btn({
  variant = "default",
  small = false,
  type = "button",
  busy = false,
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: BtnVariant; small?: boolean; busy?: boolean }): ReactElement {
  return (
    <button type={type} className={`btn btn-${variant}${small ? " btn-small" : ""}${busy ? " is-busy" : ""}`} {...rest}>
      {busy ? <span className="spinner" aria-hidden="true" /> : null}
      {children}
    </button>
  );
}

/** A button whose click asks for confirmation in a modal before running. */
export function ConfirmBtn({
  question,
  confirmLabel = "CONFIRM",
  variant = "danger",
  small = false,
  busy = false,
  onConfirm,
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  question: string;
  confirmLabel?: string;
  variant?: BtnVariant;
  small?: boolean;
  busy?: boolean;
  onConfirm: () => Promise<void> | void;
}): ReactElement {
  const [open, setOpen] = useState(false);
  const [running, setRunning] = useState(false);
  const [fail, setFail] = useState("");
  const run = async (): Promise<void> => {
    setRunning(true);
    setFail("");
    try {
      await onConfirm();
      setOpen(false);
    } catch (err) {
      setFail(err instanceof ApiError ? (err.code !== "" && err.code !== "unknown" ? `${err.message} (${err.code})` : err.message) : "The request failed.");
    } finally {
      setRunning(false);
    }
  };
  const busyNow = busy || running;
  return (
    <>
      <Btn variant={variant} small={small} {...rest} onClick={() => setOpen(true)}>
        {children}
      </Btn>
      {open ? (
        <Modal title={question} onClose={() => (busyNow ? undefined : setOpen(false))}>
          {fail !== "" ? (
            <p className="form-error" role="alert">
              {fail}
            </p>
          ) : null}
          <div className="confirm-row">
            <Btn variant="danger" busy={busyNow} onClick={() => void run()}>
              {confirmLabel}
            </Btn>
            <Btn variant="ghost" disabled={busyNow} onClick={() => setOpen(false)}>
              CANCEL
            </Btn>
          </div>
        </Modal>
      ) : null}
    </>
  );
}

// ---------------------------------------------------------------- form fields

export function Field({ label, hint, error, children, htmlFor }: { label: string; hint?: string; error?: string; children: ReactNode; htmlFor?: string }): ReactElement {
  return (
    <div className="field">
      <label className="field-label" htmlFor={htmlFor}>
        {label}
      </label>
      {children}
      {hint !== undefined && hint !== "" ? <p className="field-hint">{hint}</p> : null}
      {error !== undefined && error !== "" ? <p className="field-error" role="alert">{error}</p> : null}
    </div>
  );
}

function joinClass(base: string, extra: string | undefined, invalid: boolean): string {
  return [base, extra, invalid ? "is-invalid" : ""].filter((c) => c !== "" && c !== undefined).join(" ");
}

export function Input({ invalid = false, className, ...rest }: InputHTMLAttributes<HTMLInputElement> & { invalid?: boolean }): ReactElement {
  return <input className={joinClass("input", className, invalid)} {...rest} />;
}

export function TextArea({ invalid = false, className, ...rest }: TextareaHTMLAttributes<HTMLTextAreaElement> & { invalid?: boolean }): ReactElement {
  return <textarea className={joinClass("input textarea", className, invalid)} {...rest} />;
}

export function Select({ invalid = false, className, children, ...rest }: SelectHTMLAttributes<HTMLSelectElement> & { invalid?: boolean }): ReactElement {
  return (
    <select className={joinClass("input select", className, invalid)} {...rest}>
      {children}
    </select>
  );
}

export function CheckRow({ checked, onChange, label, description, disabled = false }: { checked: boolean; onChange: (next: boolean) => void; label: string; description?: string; disabled?: boolean }): ReactElement {
  return (
    <label className={`check-row${disabled ? " is-disabled" : ""}`}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span className="check-label">
        <span className="check-title">{label}</span>
        {description !== undefined && description !== "" ? <span className="check-desc">{description}</span> : null}
      </span>
    </label>
  );
}

// ---------------------------------------------------------------- modal

export function Modal({ title, onClose, children, wide = false }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }): ReactElement {
  const boxRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    boxRef.current?.focus();
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="modal-backdrop" onMouseDown={(e) => (e.target === e.currentTarget ? onClose() : undefined)}>
      <div className={`modal${wide ? " modal-wide" : ""}`} role="dialog" aria-modal="true" aria-label={title} ref={boxRef} tabIndex={-1}>
        <div className="modal-head">
          <h2>{title}</h2>
          <button type="button" className="modal-close" aria-label="CLOSE" onClick={onClose}>
            ×
          </button>
        </div>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- states

export function Loading({ label = "LOADING" }: { label?: string }): ReactElement {
  return (
    <div className="state-block" role="status">
      <span className="spinner" aria-hidden="true" />
      <span className="mono-caps">{label}</span>
    </div>
  );
}

/** A clear failure panel with the request's reason and a retry. Never a blank. */
export function Failure({ message, onRetry }: { message: string; onRetry?: () => void }): ReactElement {
  return (
    <div className="failure" role="alert">
      <p className="failure-title">FAILED TO LOAD</p>
      <p className="failure-reason">{message}</p>
      {onRetry !== undefined ? (
        <Btn variant="ghost" onClick={onRetry}>
          RETRY
        </Btn>
      ) : null}
    </div>
  );
}

export function Empty({ label }: { label: string }): ReactElement {
  return (
    <p className="empty">{label}</p>
  );
}

export function ScreenLoader({ error, loading, onRetry, children }: { error: string | null; loading: boolean; onRetry?: () => void; children: ReactNode }): ReactElement {
  if (error !== null) return <Failure message={error} onRetry={onRetry} />;
  if (loading) return <Loading />;
  return <>{children}</>;
}

// ---------------------------------------------------------------- table + bits

export function TableWrap({ children }: { children: ReactNode }): ReactElement {
  return (
    <div className="table-scroll">
      <table>{children}</table>
    </div>
  );
}

export function Pill({ tone, children }: { tone: "ok" | "warn" | "danger" | "dim"; children: ReactNode }): ReactElement {
  return <span className={`pill pill-${tone}`}>{children}</span>;
}

export function MonoLabel({ children }: { children: ReactNode }): ReactElement {
  return <span className="mono-caps">{children}</span>;
}
