/**
 * Session state for the SPA: /api/auth/me on boot, the sign-in flow, and sign-out.
 * Credentials live only in the HttpOnly cookies; nothing is stored client-side.
 */

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { ReactElement, ReactNode } from "react";
import { api, ApiError, UNAUTHORIZED_EVENT } from "./api";
import type { Me, SignInResult } from "./types";

export const PERMISSIONS: { key: string; description: string }[] = [
  { key: "content.edit", description: "Edit drafts and redirects" },
  { key: "content.publish", description: "Publish and unpublish content" },
  { key: "media.manage", description: "Upload, edit and delete media" },
  { key: "messages.read", description: "Read the message inbox" },
  { key: "messages.manage", description: "Archive and delete messages" },
  { key: "analytics.read", description: "View statistics" },
  { key: "analytics.export", description: "Export analytics as CSV" },
  { key: "settings.manage", description: "Edit site settings" },
  { key: "audit.read", description: "Read the audit log" },
  { key: "users.manage", description: "Invite and manage staff (owner-only in practice)" },
];

export type AuthState = "loading" | "authed" | "guest";

interface AuthContextValue {
  state: AuthState;
  me: Me | null;
  can: (permission: string) => boolean;
  reload: () => Promise<void>;
  signIn: (email: string, password: string) => Promise<SignInResult>;
  completeSignIn: () => Promise<void>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue>({
  state: "loading",
  me: null,
  can: () => false,
  reload: async () => undefined,
  signIn: async () => {
    throw new Error("no provider");
  },
  completeSignIn: async () => undefined,
  signOut: async () => undefined,
});

export function AuthProvider({ children }: { children: ReactNode }): ReactElement {
  const [state, setState] = useState<AuthState>("loading");
  const [me, setMe] = useState<Me | null>(null);

  const reload = useCallback(async (): Promise<void> => {
    try {
      const info = await api<Me>("GET", "/api/auth/me");
      setMe(info);
      setState("authed");
    } catch (err) {
      // The api client already tried one refresh before surfacing the 401.
      if (err instanceof ApiError && err.status === 401) {
        setMe(null);
        setState("guest");
        return;
      }
      // Network failure at boot: stay out rather than flash a broken shell.
      setMe(null);
      setState("guest");
    }
  }, []);

  useEffect(() => {
    void reload();
    const onUnauthorized = (): void => {
      setMe(null);
      setState("guest");
    };
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized);
  }, [reload]);

  const can = useCallback(
    (permission: string): boolean => {
      if (!me) return false;
      if (me.user.owner) return true;
      return me.permissions.includes(permission);
    },
    [me],
  );

  const signIn = useCallback(async (email: string, password: string): Promise<SignInResult> => {
    const res = await api<{ next?: string; token?: string } | undefined>("POST", "/api/auth/signin", { email, password });
    if (res !== undefined && res.next === "2fa" && typeof res.token === "string") {
      return { kind: "2fa", token: res.token };
    }
    await reload();
    return { kind: "signed_in" };
  }, [reload]);

  const completeSignIn = useCallback(async (): Promise<void> => {
    await reload();
  }, [reload]);

  const signOut = useCallback(async (): Promise<void> => {
    try {
      await api("POST", "/api/auth/signout");
    } catch {
      // Cookies are being cleared server-side; even a failed call must not keep the SPA open.
    }
    setMe(null);
    setState("guest");
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({ state, me, can, reload, signIn, completeSignIn, signOut }),
    [state, me, can, reload, signIn, completeSignIn, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  return useContext(AuthContext);
}

// ---------------------------------------------------------------- passkey plumbing

/** Base64url / base64 (padded or not) to ArrayBuffer. */
export function b64ToBuffer(value: string): ArrayBuffer {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padded = normalized + "=".repeat((4 - (normalized.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

export function bufferToB64(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function coerceCredentialId(id: string): BufferSource {
  return b64ToBuffer(id);
}

/** Server options JSON (buffers as base64url strings) -> WebAuthn API structures. */
export function toCreationOptions(options: Record<string, unknown>): PublicKeyCredentialCreationOptions {
  const user = options.user as { id: string; name?: string; displayName?: string } | undefined;
  const exclude = (options.excludeCredentials as { id: string; type?: string; transports?: string[] }[] | undefined) ?? [];
  return {
    challenge: b64ToBuffer(String(options.challenge ?? "")),
    rp: (options.rp as PublicKeyCredentialRpEntity | undefined) ?? { name: window.location.hostname },
    user: {
      id: coerceCredentialId(user?.id ?? ""),
      name: user?.name ?? "",
      displayName: user?.displayName ?? "",
    },
    pubKeyCredParams: (options.pubKeyCredParams as PublicKeyCredentialParameters[] | undefined) ?? [],
    timeout: typeof options.timeout === "number" ? options.timeout : undefined,
    excludeCredentials: exclude.map((c) => ({
      id: coerceCredentialId(c.id),
      type: "public-key" as const,
      transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
    authenticatorSelection: options.authenticatorSelection as AuthenticatorSelectionCriteria | undefined,
    attestation: (options.attestation as AttestationConveyancePreference | undefined) ?? "none",
  };
}

export function toRequestOptions(options: Record<string, unknown>): PublicKeyCredentialRequestOptions {
  const allow = (options.allowCredentials as { id: string; type?: string; transports?: string[] }[] | undefined) ?? [];
  return {
    challenge: b64ToBuffer(String(options.challenge ?? "")),
    rpId: typeof options.rpId === "string" ? options.rpId : undefined,
    timeout: typeof options.timeout === "number" ? options.timeout : undefined,
    allowCredentials: allow.map((c) => ({
      id: coerceCredentialId(c.id),
      type: "public-key" as const,
      transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
    userVerification: (options.userVerification as UserVerificationRequirement | undefined) ?? "required",
  };
}

export function encodeAttestation(
  credential: PublicKeyCredential,
): {
  id: string;
  rawId: string;
  type: string;
  response: {
    clientDataJSON: string;
    attestationObject: string;
    transports?: string[];
  };
} {
  const response = credential.response as AuthenticatorAttestationResponse;
  const transports =
    typeof (credential.response as AuthenticatorAttestationResponse & { getTransports?: () => string[] }).getTransports === "function"
      ? (credential.response as AuthenticatorAttestationResponse & { getTransports: () => string[] }).getTransports()
      : undefined;
  return {
    id: credential.id,
    rawId: bufferToB64(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: bufferToB64(response.clientDataJSON),
      attestationObject: bufferToB64(response.attestationObject),
      transports,
    },
  };
}

export function encodeAssertion(
  credential: PublicKeyCredential,
): {
  id: string;
  rawId: string;
  type: string;
  response: {
    clientDataJSON: string;
    authenticatorData: string;
    signature: string;
    userHandle: string | null;
  };
} {
  const response = credential.response as AuthenticatorAssertionResponse;
  return {
    id: credential.id,
    rawId: bufferToB64(credential.rawId),
    type: credential.type,
    response: {
      clientDataJSON: bufferToB64(response.clientDataJSON),
      authenticatorData: bufferToB64(response.authenticatorData),
      signature: bufferToB64(response.signature),
      userHandle: response.userHandle ? bufferToB64(response.userHandle) : null,
    },
  };
}
