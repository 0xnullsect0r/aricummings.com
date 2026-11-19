# Design system — dark brutalist mono catalog

Source: ui-ux-pro-max search (Brutalism, variance 7, motion 2, density 4) with owner overrides:
dark-only ground instead of bone white, signal-orange accent instead of the suggested
Tailwind-blue, no scroll-storytelling pattern (printed-catalog structure instead), no GSAP
(CSS only). Banned list from the skill applies on top.

## Direction, one sentence

A printed parts catalog rendered on a dark terminal: everything is a 1px-bordered cell in a
strict grid, set in Space Mono, indexed like a stock list, with one signal-orange accent and
zero ornament.

## Palette (dark only — there is no light mode)

| Token | Value | Job |
| --- | --- | --- |
| `--ground` | `#0A0A0B` | page background |
| `--raised` | `#121214` | raised cells, inputs |
| `--raise2` | `#1A1A1D` | hover fill, table stripes |
| `--bone` | `#EDEDE6` | primary text |
| `--bone-dim` | `#B9B9B0` | secondary text |
| `--muted` | `#85857C` | labels, captions (AA on ground) |
| `--line` | `#2C2C30` | quiet dividers |
| `--line-strong` | `#4A4A50` | input borders |
| `--edge` | `#EDEDE6` | structural 1px borders (bone) |
| `--accent` | `#FF4D00` | signal orange — the only loud thing |
| `--accent-bright` | `#FF6A26` | accent hover |
| `--accent-ink` | `#0A0A0B` | text on accent fills |
| `--danger` | `#FF3B3B` | destructive |
| `--ok` | `#37D67A` | success states only |

Rules: bone `--edge` borders for structural frames (header, sections, table frames);
`--line` inside. Accent is allowed on: links, active markers, one rule per page, selection
fill, the caret. Never a gradient, never text on `--ground` below AA (body-size accent text
uses `--accent-bright`). Admin uses the same tokens, quieter: fewer bone borders, more
`--line`, no decorative accent.

Contrast verified at build time with a script (target AA): bone 15.4:1, bone-dim 9.1:1,
muted 5.3:1, accent-bright on ground 4.6:1, accent fills use accent-ink 4.7:1.

## Type

- **Space Mono** 400/700 (`@fontsource/space-mono`): h1–h3, nav, labels, table headers,
  code, all UI chrome. Uppercase + letter-spacing 0.08em for labels at 11–12px.
- **IBM Plex Sans** 400/500/600 (`@fontsource/ibm-plex-sans`): body copy, long work-page
  prose, form inputs.
- Scale (mono headings): h1 `clamp(2.75rem, 7vw, 5.5rem)/0.95`, h2 `clamp(1.5rem,3vw,2.25rem)`,
  h3 `1.125rem`. Body `1.0625rem/1.65`. Two families, nothing else, no orphan weights.

## Layout

- One 12-col grid, `max-width: 72rem`, gutters 1px lines where columns meet (visible grid).
- Header: full-bleed rule top and bottom, wordmark left `ARI CUMMINGS` mono 700, nav right,
  one GitHub link. Sticky, ground bg, bottom bone border.
- Work index: numbered rows (`01`, `02`, …) — full-width rows, not card grids. Row = number,
  name (mono 700), one-line descriptor, tech tags right, arrow. Row borders 1px `--line`,
  hover fills `--raise2` and the arrow turns accent.
- Sections separated by 1px bone rules with a mono uppercase label sitting on the rule
  (`— SELECTED WORK —` style: label left, rule continues).
- Spacing scale: 4px base → 8, 16, 24, 32, 48, 64, 96.
- Sharp corners everywhere (radius 0). Focus: 2px `--accent` outline offset 2px. Targets
  ≥44px. `prefers-reduced-motion`: all transitions off (they're instant anyway).

## Signature moment

The GitHub strip: a live "system status" block near the footer set like a terminal readout —
`GITHUB: 120 PUBLIC REPOS / RUST 38 · C# 21 · SWIFT 9 / ACCOUNT SINCE 2024-01` — rendered in
mono on one line that wraps like a ticker, with a bone rule above and the data cached from
the sync job (never client-side).

## Banned (skill list, enforced)

No purple-blue gradients, no gradient text, no glassmorphism, no emoji bullets, no
centered-everything, no icon+title+two-line card grids, no sparkles/robots/blobs, no shadows
on everything, no filler copy (elevate/unlock/seamless/…), no fake stats.

## Honest content rules

Every number shown must come from the GitHub cache or a real fact. Project status words are
the real ones (pre-release, in progress, unaudited). TBD where unknown, never invented.
