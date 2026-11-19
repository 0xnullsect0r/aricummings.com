import type { Metadata, Viewport } from "next";
import "./globals.css";

// Every render needs its per-request nonce, so nothing is prerendered.
export const dynamic = "force-dynamic";

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  themeColor: "#0A0A0B",
};

export const metadata: Metadata = {
  title: "ARI CUMMINGS",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body className="flex min-h-dvh flex-col bg-ground font-sans text-bone antialiased">
        {children}
      </body>
    </html>
  );
}
