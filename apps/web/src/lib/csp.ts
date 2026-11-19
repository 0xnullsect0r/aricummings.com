// Nonce CSP, built per request in src/proxy.ts.
// script-src carries the nonce; styles are 'self' 'unsafe-inline' because Next
// and Tailwind inject <style> tags without a nonce, and style-src-attr stays
// open for things like an image's aspect ratio.

export const createNonce = (): string =>
  btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));

export const contentSecurityPolicy = (nonce: string, dev = false): string =>
  [
    "default-src 'self'",
    "img-src 'self' data:",
    `connect-src 'self'${dev ? " ws:" : ""}`,
    "font-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'self'",
    `script-src 'self' 'nonce-${nonce}'${dev ? " 'unsafe-eval'" : ""}`,
    "style-src 'self' 'unsafe-inline'",
    "style-src-attr 'unsafe-inline'",
  ].join(";");
