/**
 * The admin chrome: skip link, grouped nav filtered by permission, topbar with
 * the signed-in identity and sign-out, and the content area the router mounts
 * screens into.
 */

import { useEffect } from "react";
import type { ReactElement, ReactNode } from "react";
import { api } from "./api";
import { useAuth } from "./auth";
import { Link, useRouter } from "./router";
import { Btn, useAsync } from "./ui";
import type { Overview } from "./types";

interface NavItem {
  label: string;
  to: string;
  permission: string | null; // null: any signed-in staff member
}

interface NavGroup {
  label: string;
  items: NavItem[];
}

export const NAV: NavGroup[] = [
  {
    label: "OVERVIEW",
    items: [
      { label: "Dashboard", to: "", permission: null },
      { label: "Statistics", to: "statistics", permission: "analytics.read" },
    ],
  },
  {
    label: "SITE",
    items: [
      { label: "Content", to: "content", permission: "content.edit" },
      { label: "Media", to: "media", permission: "media.manage" },
      { label: "SEO", to: "seo", permission: "content.edit" },
      { label: "Redirects", to: "redirects", permission: "content.edit" },
    ],
  },
  {
    label: "INBOX",
    items: [{ label: "Messages", to: "messages", permission: "messages.read" }],
  },
  {
    label: "SYSTEM",
    items: [
      { label: "Settings", to: "settings", permission: "settings.manage" },
      { label: "Users", to: "users", permission: "users.manage" },
      { label: "Audit log", to: "audit", permission: "audit.read" },
    ],
  },
  {
    label: "ACCOUNT",
    items: [{ label: "Account", to: "account", permission: null }],
  },
];

/** Which permission a route needs before the SPA will even mount its screen. */
export function permissionForRoute(path: string): string | null {
  const first = path.split("/")[0] ?? "";
  for (const group of NAV) {
    for (const item of group.items) {
      if (item.to === first) return item.permission;
    }
  }
  if (first === "") return null;
  return null;
}

export function Shell({ path, children }: { path: string; children: ReactNode }): ReactElement {
  const { me, can, signOut } = useAuth();
  const router = useRouter();
  const overview = useAsync<Overview | null>(
    async () => (can("messages.read") ? api<Overview>("GET", "/api/admin/overview") : null),
    [can("messages.read")],
  );

  const first = path.split("/")[0] ?? "";
  const unread = overview.data !== null ? overview.data.messages.unread : null;

  useEffect(() => {
    document.title = "ARI CUMMINGS — ADMIN";
  }, []);

  return (
    <div className="ari-frame">
      <a className="ari-skip" href="#ari-main">
        SKIP TO CONTENT
      </a>
      <nav className="ari-side" aria-label="ADMIN SECTIONS">
        <div className="ari-side-wordmark">
          ARI CUMMINGS <span>ADMIN</span>
        </div>
        {NAV.map((group) => {
          const items = group.items.filter((item) => item.permission === null || can(item.permission));
          if (items.length === 0) return null;
          return (
            <div className="nav-group" key={group.label}>
              <div className="nav-group-label">{group.label}</div>
              {items.map((item) => (
                <Link
                  key={item.to}
                  to={item.to}
                  className="nav-link"
                  aria-current={first === item.to ? "page" : undefined}
                >
                  {item.label}
                  {item.to === "messages" && unread !== null && unread > 0 ? (
                    <span className="nav-count">{unread}</span>
                  ) : null}
                </Link>
              ))}
            </div>
          );
        })}
      </nav>
      <div className="ari-main">
        <header className="ari-topbar">
          <span className="ari-topbar-user">
            <strong>{me?.user.email ?? ""}</strong>
            {me?.user.owner === true ? " · OWNER" : ""}
          </span>
          <Btn variant="ghost" onClick={() => void signOut().then(() => router.navigate("", true))}>
            SIGN OUT
          </Btn>
        </header>
        <main className="ari-content" id="ari-main">
          {children}
        </main>
      </div>
    </div>
  );
}
