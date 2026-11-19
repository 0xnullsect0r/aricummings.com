// Server-component helper: pulls the preview token and nonce that src/proxy.ts
// set on the request, and loads the snapshot for this render.

import { headers } from "next/headers";
import { getSnapshot } from "@/lib/content";

export interface RequestSnapshot {
  snapshot: Awaited<ReturnType<typeof getSnapshot>>;
  preview: boolean;
  nonce: string | undefined;
}

export async function requestSnapshot(): Promise<RequestSnapshot> {
  const requestHeaders = await headers();
  const token = requestHeaders.get("x-preview-token") ?? undefined;
  const nonce = requestHeaders.get("x-nonce") ?? undefined;
  const snapshot = await getSnapshot(token);
  return { snapshot, preview: Boolean(token), nonce };
}
