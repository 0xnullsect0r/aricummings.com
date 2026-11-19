"use client";

// Fixed loader route for the client-only admin SPA (per docs/brief.md).
// Rendering stays dynamic and noindex via the admin layout (server component).
// src/features/admin is owned by the admin-spa area; until it lands,
// src/types/admin-module.d.ts keeps the build green.
import dynamic from "next/dynamic";

export default dynamic(() => import("@/features/admin").then((m) => m.default), { ssr: false });
