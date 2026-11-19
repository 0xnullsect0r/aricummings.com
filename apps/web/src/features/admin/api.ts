/**
 * The single API client for the admin SPA.
 *
 * - same-origin fetch with credentials (HttpOnly cookies carry everything)
 * - one automatic POST /api/auth/refresh retry on 401 (single-flight), then sign-out
 * - one typed error shape { error, code } matching the API pipeline
 * - never logs request or response bodies, so tokens never reach the console
 */

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, message: string, code: string) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
  }
}

export const UNAUTHORIZED_EVENT = "ari:unauthorized";

type Json = unknown;

let refreshInFlight: Promise<boolean> | null = null;

/** POST /api/auth/refresh once; concurrent 401s share the same attempt. */
function refreshOnce(): Promise<boolean> {
  if (!refreshInFlight) {
    refreshInFlight = (async () => {
      try {
        const res = await fetch("/api/auth/refresh", {
          method: "POST",
          credentials: "same-origin",
        });
        return res.ok;
      } catch {
        return false;
      } finally {
        // Give the rotation grace a beat before another tab may retry.
        setTimeout(() => {
          refreshInFlight = null;
        }, 0);
      }
    })();
  }
  return refreshInFlight;
}

function announceUnauthorized(): void {
  window.dispatchEvent(new CustomEvent(UNAUTHORIZED_EVENT));
}

function parseBody(status: number, text: string): Json {
  if (text.length === 0) return undefined;
  try {
    return JSON.parse(text) as Json;
  } catch {
    throw new ApiError(status, text.slice(0, 300), "bad_response");
  }
}

function asError(status: number, body: Json): ApiError {
  if (body !== null && typeof body === "object") {
    const rec = body as Record<string, unknown>;
    const error = typeof rec.error === "string" ? rec.error : "Request failed.";
    const code = typeof rec.code === "string" ? rec.code : "unknown";
    return new ApiError(status, error, code);
  }
  return new ApiError(status, "Request failed.", "unknown");
}

/** Endpoints that must never trigger the refresh dance (they are the auth flow). */
const AUTH_PATHS = ["/api/auth/signin", "/api/auth/2fa", "/api/auth/refresh", "/api/auth/signout"];

export interface RequestOptions {
  /** Query string, already encoded, starting with "?". */
  query?: string;
  /** Set when the caller already handled one refresh retry (internal). */
  isRetry?: boolean;
  signal?: AbortSignal;
}

export async function api<T>(
  method: string,
  path: string,
  body?: Json | FormData,
  options: RequestOptions = {},
): Promise<T> {
  let res: Response;
  try {
    const init: RequestInit = {
      method,
      credentials: "same-origin",
      signal: options.signal,
    };
    if (body !== undefined && body !== null) {
      if (body instanceof FormData) {
        init.body = body;
      } else {
        init.headers = { "content-type": "application/json" };
        init.body = JSON.stringify(body);
      }
    }
    res = await fetch(options.query ? path + options.query : path, init);
  } catch (err) {
    if (err instanceof DOMException && err.name === "AbortError") throw err;
    throw new ApiError(0, "Could not reach the server.", "network");
  }

  if (res.status === 401 && !options.isRetry && !AUTH_PATHS.some((p) => path === p)) {
    const refreshed = await refreshOnce();
    if (refreshed) return api<T>(method, path, body, { ...options, isRetry: true });
    announceUnauthorized();
  }

  const text = await res.text();
  const parsed = parseBody(res.status, text);
  if (!res.ok) throw asError(res.status, parsed);
  return parsed as T;
}

/** Multipart upload with progress; XHR because fetch has no upload progress. */
export function upload<T>(path: string, file: File, fieldName: string, onProgress: (fraction: number) => void): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const send = (isRetry: boolean): void => {
      const xhr = new XMLHttpRequest();
      xhr.open("POST", path);
      xhr.withCredentials = true;
      xhr.responseType = "text";
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable) onProgress(e.loaded / e.total);
      };
      xhr.onerror = () => reject(new ApiError(0, "Could not reach the server.", "network"));
      xhr.onload = async () => {
        if (xhr.status === 401 && !isRetry) {
          const refreshed = await refreshOnce();
          if (refreshed) {
            send(true);
            return;
          }
          announceUnauthorized();
        }
        const parsed = parseBody(xhr.status, xhr.responseText ?? "");
        if (!(xhr.status >= 200 && xhr.status < 300)) {
          reject(asError(xhr.status, parsed));
          return;
        }
        resolve(parsed as T);
      };
      const form = new FormData();
      form.append(fieldName, file);
      xhr.send(form);
    };
    send(false);
  });
}

/** Simple string query builder, skipping empty values. */
export function query(params: Record<string, string | number | undefined>): string {
  const parts: string[] = [];
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === "") continue;
    parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`);
  }
  return parts.length === 0 ? "" : `?${parts.join("&")}`;
}
