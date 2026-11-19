"use client";

import { useEffect } from "react";

// Emits the contact conversion into the analytics beacon (always counted).
export function ConversionPing() {
  useEffect(() => {
    window.dispatchEvent(
      new CustomEvent("ari:track", { detail: { t: "conversion", label: "contact" } })
    );
  }, []);
  return null;
}
