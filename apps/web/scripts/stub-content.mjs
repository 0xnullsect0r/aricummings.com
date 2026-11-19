// Tiny stub of the API's internal content listener for local verification.
// Serves GET /internal/content (ETag + If-None-Match -> 304) and
// /internal/content/version from a seed JSON in the pinned snapshot shape.
// Run: node scripts/stub-content.mjs   (listens on 127.0.0.1:8131)
import { createHash } from "node:crypto";
import { createServer } from "node:http";

const PORT = 8131;

// Reference snapshot shape (draft values - copy review is Ari's, see ledger).
const snapshot = {
  version: Date.now(),
  documents: {
    site: {
      name: "Ari Cummings",
      tagline: "software developer",
      contactEmail: "contact@aricummings.com",
      githubHandle: "0xnullsect0r",
      location: "McMinnville, OR",
      footerLicense: "AGPL-3.0",
      seo: { url: "http://localhost:8130", title: "Ari Cummings — software developer", description: "Portfolio of Ari Cummings, software developer in McMinnville, OR." },
    },
    "page:home": {
      hero: {
        kicker: "PORTFOLIO",
        lines: ["BUILDS SOFTWARE", "SHIPS PROJECTS"],
        intro: "Software developer building desktop tools, operating systems internals, and client systems.",
        roles: ["software developer"],
      },
      about: {
        paras: [
          "I am Ari Cummings, a software developer based in McMinnville, Oregon. I work across desktop apps, systems programming, and full-stack client builds.",
          "I got into programming at Delphian School and kept going through computer science coursework in college. Today most of my time goes into the projects indexed below.",
        ],
        facts: [
          { label: "School", value: "Delphian School" },
          { label: "Based", value: "McMinnville, OR" },
          { label: "Studied", value: "CS in college" },
        ],
      },
      clientIntro: "Client systems built while working with Artistech Endeavors.",
      alsoBuilding: [
        { name: "Quark", url: "", note: "in progress" },
        { name: "fleetboard", url: "", note: "in progress" },
        { name: "the-perimeter", url: "", note: "pre-release" },
        { name: "PointsSlip", url: "", note: "" },
        { name: "MileageTracker", url: "", note: "" },
        { name: "Tailscale-Ubiquiti", url: "", note: "homelab tooling" },
        { name: "WatchArcade", url: "", note: "" },
      ],
      stack: [
        { label: "Systems", items: ["Rust", "C", "Assembly"] },
        { label: "Apps", items: ["C# / .NET", "Swift / SwiftUI", "Kotlin", "Electron / Node"] },
        { label: "Web + Ops", items: ["TypeScript / Next.js", "Python", "Docker Compose", "Nginx", "Proxmox"] },
      ],
      workOrder: ["enclave", "rustos", "multiforge", "edex", "red-wagon-rentals", "connected-riding"],
    },
    "work:enclave": {
      title: "Enclave",
      status: "pre-release",
      category: "oss",
      year: 2025,
      summary: "Encrypted local-first vault for sensitive files.",
      links: [{ label: "Repository", url: "https://github.com/0xnullsect0r/enclave" }],
      tech: ["Rust", "SQLite", "Crypto"],
      repo: { url: "https://github.com/0xnullsect0r/enclave", license: "AGPL-3.0" },
      sections: [
        { type: "text", heading: "What it is", html: "<p>Enclave is a local-first, encrypted vault. Keys never leave the machine.</p><ul><li>AEAD file encryption</li><li>Plaintext never hits disk unencrypted</li></ul>" },
        { type: "photos", items: [{ image: "a1b2c3d4e5f60718", alt: "Enclave main window", caption: "Vault overview" }] },
        { type: "link", label: "Releases", url: "https://github.com/0xnullsect0r/enclave/releases" },
      ],
    },
    "work:rustos": {
      title: "RustOS",
      status: "in progress",
      category: "oss",
      year: 2024,
      summary: "A small operating system kernel written in Rust.",
      tech: ["Rust", "x86", "QEMU"],
      repo: { url: "https://github.com/0xnullsect0r/rustos", license: "MIT" },
      sections: [
        { type: "text", heading: "Scope", html: "<p>A teaching-myself-kernels project: bootloader handoff, paging, and a tiny scheduler.</p>" },
      ],
    },
    "work:multiforge": {
      title: "MultiForge",
      status: "pre-release",
      category: "oss",
      year: 2025,
      summary: "Minecraft NeoForge server orchestration and mod management.",
      tech: ["Java", "NeoForge", "Docker"],
      repo: { url: "https://github.com/0xnullsect0r/multiforge", license: "GPL-3.0" },
      sections: [
        { type: "text", heading: "What it does", html: "<p>Spins up and manages NeoForge servers with modpack installs and update automation.</p>" },
      ],
    },
    "work:edex": {
      title: "eDEX",
      status: "maintained",
      category: "oss",
      year: 2023,
      summary: "Contributions to the eDEX-ASCII terminal emulator ecosystem.",
      tech: ["Node", "Electron"],
      repo: { url: "https://github.com/0xnullsect0r", license: "GPL-3.0" },
      sections: [
        { type: "text", heading: "Involvement", html: "<p>Port work and fixes on the QOA-side terminal project.</p>" },
      ],
    },
    "work:red-wagon-rentals": {
      title: "Red Wagon Rentals",
      status: "delivered",
      category: "client",
      year: 2024,
      summary: "Rental booking site for a local wagon-rental business.",
      tech: ["Next.js", "PostgreSQL"],
      sections: [],
    },
    "work:connected-riding": {
      title: "Connected Riding",
      status: "delivered",
      category: "client",
      year: 2024,
      summary: "Horse-riding instruction scheduling and client portal.",
      tech: ["ASP.NET Core", "Avalonia"],
      sections: [],
    },
  },
  redirects: [{ from: "/old-work", to: "/#work" }],
  media: {
    a1b2c3d4e5f60718: { w: 1920, h: 1080, widths: [400, 800, 1200], og: true },
  },
  github: {
    profile: { login: "0xnullsect0r", created_at: "2021-03-08T12:00:00Z", public_repos: 34 },
    repos: [
      ...Array.from({ length: 11 }, () => ({ name: "rust-proj", language: "Rust" })),
      ...Array.from({ length: 8 }, () => ({ name: "csharp-proj", language: "C#" })),
      ...Array.from({ length: 6 }, () => ({ name: "ts-proj", language: "TypeScript" })),
      ...Array.from({ length: 4 }, () => ({ name: "swift-proj", language: "Swift" })),
      { name: "dotfiles", language: "Shell" },
    ],
    fetchedAt: Date.now(),
  },
};

const body = JSON.stringify(snapshot);
const etag = `"${createHash("sha256").update(body).digest("hex").slice(0, 16)}"`;

const server = createServer((req, res) => {
  const url = new URL(req.url ?? "/", `http://127.0.0.1:${PORT}`);
  if (req.method === "GET" && url.pathname === "/internal/content") {
    // Preview: token "good" gets a draft snapshot, anything else 403.
    const preview = url.searchParams.get("preview");
    if (preview !== null) {
      if (preview !== "good") {
        res.writeHead(403, { "content-type": "application/json" });
        res.end(JSON.stringify({ error: "preview expired" }));
        return;
      }
      const draft = JSON.parse(body);
      draft.documents["work:enclave"].title = "Enclave (draft)";
      const draftBody = JSON.stringify(draft);
      res.writeHead(200, { "content-type": "application/json", "cache-control": "no-store" });
      res.end(draftBody);
      return;
    }
    if (req.headers["if-none-match"] === etag) {
      res.writeHead(304, { etag });
      res.end();
      return;
    }
    res.writeHead(200, {
      "content-type": "application/json",
      "cache-control": "no-store",
      etag,
    });
    res.end(body);
    return;
  }
  if (req.method === "GET" && url.pathname === "/internal/content/version") {
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ version: snapshot.version }));
    return;
  }
  res.writeHead(404, { "content-type": "application/json" });
  res.end(JSON.stringify({ error: "not found" }));
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`stub content server on http://127.0.0.1:${PORT} (etag ${etag})`);
});
