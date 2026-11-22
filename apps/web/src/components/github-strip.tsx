import type { GithubData, GithubProfile } from "@/lib/content";

// The signature moment: a terminal-readout line from the GitHub sync cache in
// the content snapshot. Never client-side. Hidden when github is null. Every
// number shown comes from the cache; unknowns are omitted, never invented.

export function GithubStrip({ github }: { github: GithubData | null | undefined }) {
  if (!github) return null;

  const profile: GithubProfile =
    github.profile && typeof github.profile === "object" ? github.profile : {};
  const repos = Array.isArray(github.repos) ? github.repos : [];

  // The profile holds GitHub's own total; the repo list is only a fallback.
  const repoCount =
    typeof profile.public_repos === "number" && profile.public_repos > 0
      ? profile.public_repos
      : repos.length;
  if (repoCount === 0) return null;

  const languageCounts = new Map<string, number>();
  for (const repo of repos) {
    const language = typeof repo.language === "string" ? repo.language.trim() : "";
    if (!language) continue;
    languageCounts.set(language, (languageCounts.get(language) ?? 0) + 1);
  }
  const languages = [...languageCounts.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .slice(0, 3)
    .map(([name, count]) => `${name.toUpperCase()} ${count}`)
    .join(" \u00b7 ");

  let since = "";
  if (typeof profile.created_at === "string") {
    const match = /^(\d{4})-(\d{2})/.exec(profile.created_at);
    if (match) since = `${match[1]}-${match[2]}`;
  } else if (typeof profile.created === "number" && profile.created > 0) {
    const date = new Date(profile.created * 1000);
    if (!Number.isNaN(date.getTime())) {
      since = date.toISOString().slice(0, 7);
    }
  }

  return (
    <section aria-label="GitHub activity" data-track="GITHUB">
      <div className="border-t border-edge pt-3">
        <p className="break-words font-mono text-xs leading-relaxed text-bone-dim md:text-sm">
          <span className="text-muted">GITHUB:</span>{" "}
          <span className="text-bone">
            {repoCount} PUBLIC REPO{repoCount === 1 ? "" : "S"}
          </span>
          {languages && (
            <>
              {" "}
              <span className="text-muted">/</span> <span className="text-bone">{languages}</span>
            </>
          )}
          {since && (
            <>
              {" "}
              <span className="text-muted">/</span>{" "}
              <span className="text-bone">ACCOUNT SINCE {since}</span>
            </>
          )}
        </p>
      </div>
    </section>
  );
}
