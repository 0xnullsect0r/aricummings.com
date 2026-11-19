// The web container's own edge: per-request nonce CSP, surface check,
// trailing-slash 301s, saved redirects, and the unknown-path 404 rewrite to a
// real /not-found route (with 404 status). Order of operations per the skill:
// refuse admin paths when the surface header says public, strip trailing
// slashes, handle the preview parameter, resolve content paths, then rewrite
// to not-found if missing.

import { NextResponse, type NextRequest } from "next/server";
import { contentSecurityPolicy, createNonce } from "@/lib/csp";
import { SnapshotUnavailableError, getSnapshot, resolvePublicPath } from "@/lib/content";

const ADMIN_PATH_PREFIXES = ["/admin", "/api/admin", "/api/auth"];

function isAdminPath(pathname: string): boolean {
  return ADMIN_PATH_PREFIXES.some(
    (prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`)
  );
}

// Paths the proxy resolves itself; these skip the content lookup.
function isFixedPath(pathname: string): boolean {
  if (pathname === "/" || pathname === "/not-found" || pathname === "/contact/sent") return true;
  if (isAdminPath(pathname)) return true;
  if (pathname.startsWith("/_next") || pathname.startsWith("/api/") || pathname.startsWith("/media/")) {
    return true;
  }
  return /\.[a-z0-9]+$/i.test(pathname);
}

function withSecurityHeaders(res: NextResponse, nonce: string, dev: boolean): NextResponse {
  res.headers.set("content-security-policy", contentSecurityPolicy(nonce, dev));
  res.headers.set("x-content-type-options", "nosniff");
  res.headers.set("referrer-policy", "strict-origin-when-cross-origin");
  res.headers.set("x-nonce", nonce);
  return res;
}

function previewExpiredResponse(req: NextRequest, nonce: string, dev: boolean): NextResponse {
  const html =
    '<!doctype html><html lang="en"><head><meta charset="utf-8">' +
    '<meta name="viewport" content="width=device-width, initial-scale=1">' +
    "<title>Preview expired</title></head>" +
    '<body style="margin:0;background:#0A0A0B;color:#EDEDE6;font-family:monospace;padding:3rem 1.5rem">' +
    '<p style="color:#85857C;letter-spacing:0.08em;font-size:11px;text-transform:uppercase;margin:0 0 0.75rem">ERROR 403</p>' +
    '<h1 style="font-size:1.5rem;margin:0 0 1rem">PREVIEW EXPIRED</h1>' +
    '<p style="color:#B9B9B0;max-width:40rem;margin:0 0 1.5rem">This preview link has expired or is invalid. Ask for a fresh one from the admin.</p>' +
    '<a href="/" style="color:#FF6A26">&#8592; PUBLIC SITE</a>' +
    "</body></html>";
  const res = new NextResponse(html, {
    status: 403,
    headers: { "content-type": "text/html; charset=utf-8" },
  });
  return withSecurityHeaders(res, nonce, dev);
}

export default async function proxy(req: NextRequest) {
  const { pathname, search } = req.nextUrl;
  const nonce = createNonce();
  const dev = process.env.NODE_ENV !== "production";

  // 1. Surface check. The API edge sets X-Site-Surface and refuses admin paths
  //    on the public listener first; this is the second check for when the two
  //    ever disagree. Admin paths pass only on the admin or combined surface.
  //    No header at all (local dev, no edge in front) behaves like combined.
  const surface = req.headers.get("x-site-surface")?.toLowerCase();
  if (isAdminPath(pathname) && surface !== undefined && surface !== "admin" && surface !== "combined") {
    const res = NextResponse.rewrite(new URL("/not-found", req.url), { status: 404 });
    return withSecurityHeaders(res, nonce, dev);
  }

  // 2. Trailing slash -> 301 (Next's own 308 is disabled via config).
  if (pathname !== "/" && pathname.endsWith("/")) {
    const url = new URL(`${pathname.slice(0, -1)}${search}`, req.url);
    return withSecurityHeaders(NextResponse.redirect(url, 301), nonce, dev);
  }

  // 3. Preview token: noindex/no-store, token forwarded to pages via a request
  //    header, and the token validated here so an expired link gets a plain 403.
  const previewToken = req.nextUrl.searchParams.get("preview") ?? undefined;

  const requestHeaders = new Headers(req.headers);
  // Strip the privileged request headers unconditionally: a client sending
  // x-preview-token directly would otherwise render draft content without the
  // noindex/no-store the query-param path applies, and a URL-keyed cache in
  // front could serve that draft to everyone.
  requestHeaders.delete("x-preview-token");
  requestHeaders.delete("x-nonce");
  requestHeaders.set("content-security-policy", contentSecurityPolicy(nonce, dev));
  requestHeaders.set("x-nonce", nonce);
  if (previewToken) requestHeaders.set("x-preview-token", previewToken);

  const passThrough = (): NextResponse => {
    const res = NextResponse.next({ request: { headers: requestHeaders } });
    if (previewToken) {
      res.headers.set("x-robots-tag", "noindex, nofollow");
      res.headers.set("cache-control", "no-store");
    }
    return withSecurityHeaders(res, nonce, dev);
  };

  if (req.method !== "GET" && req.method !== "HEAD") return passThrough();

  if (previewToken) {
    try {
      await getSnapshot(previewToken);
    } catch (err) {
      if (!(err instanceof SnapshotUnavailableError)) {
        const name = err instanceof Error ? err.name : "";
        if (name === "PreviewExpiredError") return previewExpiredResponse(req, nonce, dev);
      }
    }
    // Draft paths may not resolve against the published snapshot; let the
    // page render from the preview snapshot instead.
    return passThrough();
  }

  // 4. Fixed paths skip the content lookup.
  if (isFixedPath(pathname)) return passThrough();

  // 5. Resolve against the content snapshot.
  let resolution: ReturnType<typeof resolvePublicPath>;
  try {
    resolution = resolvePublicPath(await getSnapshot(), pathname);
  } catch {
    const res = new NextResponse("Service unavailable", { status: 503 });
    return withSecurityHeaders(res, nonce, dev);
  }

  if (resolution.kind === "redirect") {
    const url = new URL(`${resolution.to}${search}`, req.url);
    return withSecurityHeaders(NextResponse.redirect(url, 301), nonce, dev);
  }
  if (resolution.kind === "missing") {
    const res = NextResponse.rewrite(new URL("/not-found", req.url), { status: 404 });
    return withSecurityHeaders(res, nonce, dev);
  }
  return passThrough();
}

export const config = {
  matcher: [
    "/((?!_next/static|_next/image|healthz|robots.txt|sitemap.xml|favicon.ico|icon.svg|media/).*)",
  ],
};
