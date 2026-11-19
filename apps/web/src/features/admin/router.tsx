/**
 * The SPA's own router, on pushState.
 *
 * Clean addresses: on the admin hostname the edge rewrites /users to /admin/users
 * before Next sees it, so the browser address has no prefix. When the app is
 * reached through a path (dev server, /admin base), the prefix stays. The base is
 * decided once per render from the current pathname.
 */

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { AnchorHTMLAttributes, ReactElement, ReactNode } from "react";

function computeBase(): string {
  const path = window.location.pathname;
  // "/admin", "/admin/users", "/administrator" (not ours, but prefixed routes win
  // only for the exact segment) — treat a leading /admin segment as the base.
  if (path === "/admin" || path.startsWith("/admin/") || path.startsWith("/admin?")) return "/admin";
  return "";
}

/** Path after the base, no leading slash ("" for the root screen). */
export function currentPath(): string {
  const base = computeBase();
  let path = window.location.pathname;
  if (base) {
    path = path.slice(base.length);
    if (path === "/") path = "";
  }
  if (path.startsWith("/")) path = path.slice(1);
  return path.replace(/\/+$/, "");
}

export function href(to: string): string {
  const base = computeBase();
  if (to === "" || to === "/") return base === "" ? "/" : base;
  const clean = to.startsWith("/") ? to : `/${to}`;
  return base + clean;
}

export function navigate(to: string, replace = false): void {
  const url = href(to);
  if (replace) window.history.replaceState({}, "", url);
  else window.history.pushState({}, "", url);
  window.dispatchEvent(new PopStateEvent("popstate"));
}

interface RouteState {
  path: string;
  navigate: (to: string, replace?: boolean) => void;
}

const RouteContext = createContext<RouteState>({ path: "", navigate: () => undefined });

export function RouterProvider({ children }: { children: ReactNode }): ReactElement {
  const [path, setPath] = useState<string>(() => currentPath());

  useEffect(() => {
    const onChange = (): void => {
      setPath(currentPath());
      window.scrollTo(0, 0);
    };
    window.addEventListener("popstate", onChange);
    return () => window.removeEventListener("popstate", onChange);
  }, []);

  const navigateCallback = useCallback((to: string, replace = false): void => {
    navigate(to, replace);
  }, []);

  const value = useMemo<RouteState>(() => ({ path, navigate: navigateCallback }), [path, navigateCallback]);
  return <RouteContext.Provider value={value}>{children}</RouteContext.Provider>;
}

export function useRouter(): RouteState {
  return useContext(RouteContext);
}

export function segments(path: string): string[] {
  return path.split("/").filter((s) => s !== "").map(decodeURIComponent);
}

type LinkProps = AnchorHTMLAttributes<HTMLAnchorElement> & { to: string; replace?: boolean };

/** A real anchor with a clean address; clicks stay inside the SPA. */
export function Link({ to, replace = false, onClick, ...rest }: LinkProps): ReactNode {
  return (
    <a
      href={href(to)}
      onClick={(e) => {
        onClick?.(e);
        if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        e.preventDefault();
        navigate(to, replace);
      }}
      {...rest}
    />
  );
}
