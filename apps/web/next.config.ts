import { existsSync } from "node:fs";
import path from "node:path";
import type { NextConfig } from "next";

// The admin SPA (src/features/admin) is owned by the admin-spa area and is not
// in this tree yet. While it is absent, "@/features/admin" resolves to an
// explicit not-present placeholder so `next build` stays green; once the real
// folder lands the alias switches off automatically and the SPA is used.
const adminAppPresent = existsSync(path.join(__dirname, "src", "features", "admin"));

const nextConfig: NextConfig = {
  output: "standalone",
  poweredByHeader: false,
  images: { unoptimized: true },
  skipTrailingSlashRedirect: true,
  outputFileTracingRoot: __dirname,
  turbopack: {
    root: __dirname,
    ...(adminAppPresent
      ? {}
      : { resolveAlias: { "@/features/admin": "./src/lib/admin-placeholder.tsx" } }),
  },
  ...(adminAppPresent
    ? {}
    : {
        webpack: (config) => {
          config.resolve = config.resolve ?? {};
          config.resolve.alias = {
            ...(config.resolve.alias as Record<string, string> | undefined),
            "@/features/admin": path.join(__dirname, "src", "lib", "admin-placeholder.tsx"),
          };
          return config;
        },
      }),
};

export default nextConfig;
