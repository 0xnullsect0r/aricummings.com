/**
 * The admin SPA entry. The fixed loader route in apps/web/src/app/admin/[[...slug]]/page.tsx
 * dynamic-imports this module's default export with ssr: false. Admin paths
 * arrive as /admin/<screen> (path base) or /<screen> (admin hostname, where the
 * API edge rewrites to /admin/* before Next sees it).
 */

"use client";

import type { ReactElement } from "react";
import "./styles.css";
import { AuthProvider, useAuth } from "./auth";
import { Link, RouterProvider, useRouter, segments } from "./router";
import { Shell } from "./shell";
import { SignInScreen } from "./screens/signin";
import { InvitedScreen, ResetScreen } from "./screens/one-time";
import { DashboardScreen } from "./screens/dashboard";
import { StatisticsScreen } from "./screens/statistics";
import { ContentListScreen } from "./screens/content";
import { ContentEditScreen } from "./screens/content-edit";
import { MediaScreen } from "./screens/media";
import { SeoScreen } from "./screens/seo";
import { RedirectsScreen } from "./screens/redirects";
import { MessagesScreen } from "./screens/messages";
import { SettingsScreen } from "./screens/settings";
import { UsersScreen } from "./screens/users";
import { AuditScreen } from "./screens/audit";
import { AccountScreen } from "./screens/account";
import { Panel } from "./ui";

function NoAccess({ permission }: { permission: string }): ReactElement {
  return (
    <Panel label="NO ACCESS">
      <p className="td-dim" style={{ margin: 0 }}>
        This screen needs the <span className="td-mono">{permission}</span> permission.
      </p>
      <p style={{ marginBottom: 0 }}>
        <Link to="">BACK TO DASHBOARD</Link>
      </p>
    </Panel>
  );
}

function NotFound({ path }: { path: string }): ReactElement {
  return (
    <div className="notfound">
      <h1>404</h1>
      <p className="td-dim">
        <span className="td-mono">/{path}</span> is not an admin screen.
      </p>
      <p>
        <Link to="">BACK TO DASHBOARD</Link>
      </p>
    </div>
  );
}

function ScreenSwitch(): ReactElement {
  const { can } = useAuth();
  const { path } = useRouter();
  const parts = segments(path);
  const head = parts[0];
  const second = parts[1];

  if (head === undefined) {
    return (
      <Shell path="">
        <DashboardScreen />
      </Shell>
    );
  }
  if (head === "statistics") {
    return (
      <Shell path={path}>{can("analytics.read") ? <StatisticsScreen /> : <NoAccess permission="analytics.read" />}</Shell>
    );
  }
  if (head === "content") {
    if (second !== undefined) {
      return (
        <Shell path={path}>
          {can("content.edit") ? <ContentEditScreen docKey={second} /> : <NoAccess permission="content.edit" />}
        </Shell>
      );
    }
    return (
      <Shell path={path}>{can("content.edit") ? <ContentListScreen /> : <NoAccess permission="content.edit" />}</Shell>
    );
  }
  if (head === "media") {
    return <Shell path={path}>{can("media.manage") ? <MediaScreen /> : <NoAccess permission="media.manage" />}</Shell>;
  }
  if (head === "seo") {
    return <Shell path={path}>{can("content.edit") ? <SeoScreen /> : <NoAccess permission="content.edit" />}</Shell>;
  }
  if (head === "redirects") {
    return <Shell path={path}>{can("content.edit") ? <RedirectsScreen /> : <NoAccess permission="content.edit" />}</Shell>;
  }
  if (head === "messages") {
    return <Shell path={path}>{can("messages.read") ? <MessagesScreen /> : <NoAccess permission="messages.read" />}</Shell>;
  }
  if (head === "settings") {
    return <Shell path={path}>{can("settings.manage") ? <SettingsScreen /> : <NoAccess permission="settings.manage" />}</Shell>;
  }
  if (head === "users") {
    return <Shell path={path}>{can("users.manage") ? <UsersScreen /> : <NoAccess permission="users.manage" />}</Shell>;
  }
  if (head === "audit") {
    return <Shell path={path}>{can("audit.read") ? <AuditScreen /> : <NoAccess permission="audit.read" />}</Shell>;
  }
  if (head === "account") {
    return (
      <Shell path={path}>
        <AccountScreen />
      </Shell>
    );
  }
  return (
    <Shell path={path}>
      <NotFound path={path} />
    </Shell>
  );
}

export default function AdminApp(): ReactElement {
  return (
    <AuthProvider>
      <AppGate />
    </AuthProvider>
  );
}

function AppGate(): ReactElement {
  const { state } = useAuth();
  const path = typeof window === "undefined" ? "" : window.location.pathname.replace(/^\/admin\/?/, "").replace(/^\/+|\/+$/g, "");

  // One-time links render without a session: /invited and /reset.
  if (path === "invited") {
    return <InvitedScreen />;
  }
  if (path === "reset") {
    return <ResetScreen />;
  }

  if (state === "loading") {
    return (
      <div className="ari-root">
        <div className="state-block" role="status" style={{ margin: "auto" }}>
          <span className="spinner" aria-hidden="true" />
          <span className="mono-caps">LOADING SESSION</span>
        </div>
      </div>
    );
  }
  if (state === "guest") {
    return (
      <div className="ari-root">
        <SignInScreen />
      </div>
    );
  }
  return (
    <div className="ari-root">
      <RouterProvider>
        <ScreenSwitch />
      </RouterProvider>
    </div>
  );
}
