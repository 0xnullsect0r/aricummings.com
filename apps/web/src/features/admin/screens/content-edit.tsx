/**
 * Document editor: renders each kind's schema shape from the brief.
 * site, page:home, work:<slug> — structured fields plus ordered sections[]
 * blocks (text | photos | link) with add / remove / reorder.
 *
 * "Default empty" is a missing key, never []: removing the last list item
 * removes the key, so saved documents never grow phantom empty arrays.
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import type { ReactElement } from "react";
import { api, ApiError } from "../api";
import { useAuth } from "../auth";
import { fmtDate } from "../format";
import { Link } from "../router";
import {
  Btn,
  ConfirmBtn,
  CheckRow,
  errorText,
  Field,
  Input,
  Panel,
  Pill,
  Select,
  TableWrap,
  TextArea,
  useAsync,
} from "../ui";
import type { ContentList, ContentListItem, ContentValue, DraftResponse, MediaItem, PhotoItem, RevisionsResponse, SectionBlock } from "../types";

// ---------------------------------------------------------------- draft plumbing

type Path = (string | number)[];
type Draft = Record<string, unknown>;

function cloneDraft(draft: Draft): Draft {
  return JSON.parse(JSON.stringify(draft)) as Draft;
}

function getAt(root: unknown, path: Path): unknown {
  let node = root;
  for (const step of path) {
    if (node === null || typeof node !== "object") return undefined;
    node = (node as Record<string | number, unknown>)[step];
  }
  return node;
}

interface EditorApi {
  draft: Draft;
  mutate: (fn: (root: Draft) => void) => void;
}

/** Sets a value; undefined deletes the key (used when lists empty out). */
function setAt(root: Draft, path: Path, value: unknown): void {
  if (path.length === 0) return;
  const last = path[path.length - 1] as string | number;
  const parentPath = path.slice(0, -1);
  let parent: unknown = root;
  for (const step of parentPath) {
    if (parent === null || typeof parent !== "object") return;
    parent = (parent as Record<string | number, unknown>)[step];
  }
  if (parent === null || typeof parent !== "object") return;
  const rec = parent as Record<string | number, unknown>;
  if (value === undefined) delete rec[last];
  else rec[last] = value;
}

// ---------------------------------------------------------------- schema

type FieldSpec =
  | { kind: "text"; key: string; label: string; multiline?: boolean; mono?: boolean; placeholder?: string; hint?: string }
  | { kind: "check"; key: string; label: string; hint?: string }
  | { kind: "select"; key: string; label: string; options: { value: string; label: string }[] }
  | { kind: "group"; key: string; label: string; fields: FieldSpec[] }
  | { kind: "stringlist"; key: string; label: string; itemLabel: string; itemMultiline?: boolean }
  | { kind: "objectlist"; key: string; label: string; itemLabel: string; fields: FieldSpec[]; titleOf: (item: Record<string, unknown>) => string }
  | { kind: "blocks"; key: string; label: string };

const WORK_FIELDS: FieldSpec[] = [
  { kind: "text", key: "title", label: "TITLE" },
  { kind: "text", key: "status", label: "STATUS", hint: "Real status words: pre-release, in progress, unaudited…" },
  {
    kind: "select",
    key: "category",
    label: "CATEGORY",
    options: [
      { value: "oss", label: "oss" },
      { value: "client", label: "client" },
    ],
  },
  { kind: "text", key: "year", label: "YEAR", mono: true },
  { kind: "text", key: "summary", label: "SUMMARY", multiline: true },
  {
    kind: "objectlist",
    key: "links",
    label: "LINKS",
    itemLabel: "LINK",
    titleOf: (i) => String(i.label ?? "link"),
    fields: [
      { kind: "text", key: "label", label: "LABEL" },
      { kind: "text", key: "url", label: "URL", mono: true, placeholder: "https://…" },
    ],
  },
  { kind: "stringlist", key: "tech", label: "TECH", itemLabel: "TAG" },
  {
    kind: "group",
    key: "repo",
    label: "REPO",
    fields: [
      { kind: "text", key: "url", label: "URL", mono: true, placeholder: "https://github.com/…" },
      { kind: "text", key: "license", label: "LICENSE", placeholder: "AGPL-3.0" },
    ],
  },
  { kind: "blocks", key: "sections", label: "SECTIONS" },
];

const SCHEMAS: Record<string, FieldSpec[]> = {
  site: [
    { kind: "text", key: "name", label: "NAME" },
    { kind: "text", key: "tagline", label: "TAGLINE" },
    { kind: "text", key: "contactEmail", label: "CONTACT EMAIL", mono: true },
    { kind: "text", key: "githubHandle", label: "GITHUB HANDLE", mono: true },
    { kind: "text", key: "location", label: "LOCATION" },
    { kind: "text", key: "footerLicense", label: "FOOTER LICENSE", placeholder: "AGPL-3.0" },
    {
      kind: "group",
      key: "seo",
      label: "SEO DEFAULTS",
      fields: [
        { kind: "text", key: "title", label: "TITLE" },
        { kind: "text", key: "description", label: "DESCRIPTION", multiline: true },
        { kind: "check", key: "noindex", label: "NOINDEX THE WHOLE SITE" },
      ],
    },
  ],
  "page:home": [
    {
      kind: "group",
      key: "hero",
      label: "HERO",
      fields: [
        { kind: "text", key: "kicker", label: "KICKER" },
        { kind: "stringlist", key: "lines", label: "LINES", itemLabel: "LINE" },
        { kind: "text", key: "intro", label: "INTRO", multiline: true },
      ],
    },
    {
      kind: "group",
      key: "about",
      label: "ABOUT",
      fields: [
        { kind: "stringlist", key: "paras", label: "PARAGRAPHS", itemLabel: "PARAGRAPH", itemMultiline: true },
        {
          kind: "objectlist",
          key: "facts",
          label: "FACTS",
          itemLabel: "FACT",
          titleOf: (i) => String(i.label ?? "fact"),
          fields: [
            { kind: "text", key: "label", label: "LABEL" },
            { kind: "text", key: "value", label: "VALUE" },
          ],
        },
      ],
    },
    { kind: "text", key: "clientIntro", label: "CLIENT INTRO", multiline: true },
    {
      kind: "objectlist",
      key: "alsoBuilding",
      label: "ALSO BUILDING",
      itemLabel: "PROJECT",
      titleOf: (i) => String(i.name ?? "project"),
      fields: [
        { kind: "text", key: "name", label: "NAME" },
        { kind: "text", key: "url", label: "URL", mono: true },
        { kind: "text", key: "note", label: "NOTE" },
      ],
    },
    {
      kind: "objectlist",
      key: "stack",
      label: "STACK",
      itemLabel: "GROUP",
      titleOf: (i) => String(i.label ?? "group"),
      fields: [
        { kind: "text", key: "label", label: "LABEL" },
        { kind: "stringlist", key: "items", label: "ITEMS", itemLabel: "ITEM" },
      ],
    },
    { kind: "stringlist", key: "workOrder", label: "WORK ORDER (SLUGS)", itemLabel: "SLUG" },
  ],
  work: WORK_FIELDS,
};

function schemaFor(key: string): FieldSpec[] {
  if (key === "site") return SCHEMAS.site ?? [];
  if (key === "page:home") return SCHEMAS["page:home"] ?? [];
  if (key.startsWith("work:")) return SCHEMAS.work ?? [];
  return [];
}

// ---------------------------------------------------------------- field renderers

function TextField({ spec, ctx, path }: { spec: Extract<FieldSpec, { kind: "text" }>; ctx: EditorApi; path: Path }): ReactElement {
  const value = getAt(ctx.draft, path);
  const text = typeof value === "string" ? value : "";
  const id = `f-${path.join("-")}`;
  const common = {
    id,
    value: text,
    placeholder: spec.placeholder ?? "",
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>): void =>
      ctx.mutate((root) => setAt(root, path, e.target.value)),
  };
  return (
    <Field label={spec.label} hint={spec.hint} htmlFor={id}>
      {spec.multiline === true ? (
        <TextArea {...common} />
      ) : (
        <Input type="text" className={spec.mono === true ? "input-mono" : undefined} {...common} />
      )}
    </Field>
  );
}

function CheckField({ spec, ctx, path }: { spec: Extract<FieldSpec, { kind: "check" }>; ctx: EditorApi; path: Path }): ReactElement {
  const checked = getAt(ctx.draft, path) === true;
  return (
    <CheckRow
      label={spec.label}
      description={spec.hint}
      checked={checked}
      onChange={(next) => ctx.mutate((root) => setAt(root, path, next === true ? true : undefined))}
    />
  );
}

function SelectField({ spec, ctx, path }: { spec: Extract<FieldSpec, { kind: "select" }>; ctx: EditorApi; path: Path }): ReactElement {
  const value = getAt(ctx.draft, path);
  const text = typeof value === "string" ? value : "";
  const id = `f-${path.join("-")}`;
  return (
    <Field label={spec.label} htmlFor={id}>
      <Select
        id={id}
        value={text}
        onChange={(e) => ctx.mutate((root) => setAt(root, path, e.target.value === "" ? undefined : e.target.value))}
      >
        <option value="">—</option>
        {spec.options.map((opt) => (
          <option key={opt.value} value={opt.value}>
            {opt.label}
          </option>
        ))}
      </Select>
    </Field>
  );
}

function ListRowActions({ onRemove, onUp, onDown }: { onRemove: () => void; onUp: () => void; onDown: () => void }): ReactElement {
  return (
    <div className="row-actions">
      <Btn variant="ghost" small onClick={onUp} aria-label="MOVE UP" title="MOVE UP">
        ↑
      </Btn>
      <Btn variant="ghost" small onClick={onDown} aria-label="MOVE DOWN" title="MOVE DOWN">
        ↓
      </Btn>
      <Btn variant="ghost" small onClick={onRemove} aria-label="REMOVE" title="REMOVE">
        REMOVE
      </Btn>
    </div>
  );
}

function StringListField({ spec, ctx, path }: { spec: Extract<FieldSpec, { kind: "stringlist" }>; ctx: EditorApi; path: Path }): ReactElement {
  const raw = getAt(ctx.draft, path);
  const items = Array.isArray(raw) ? (raw as unknown[]) : [];
  const write = (next: unknown[]): void => ctx.mutate((root) => setAt(root, path, next.length === 0 ? undefined : next));
  return (
    <div className="field">
      <span className="field-label">{spec.label}</span>
      <div className="list-editor">
        {items.map((item, i) => (
          <div className="list-editor-row" key={i}>
            <span className="list-editor-index">{String(i + 1).padStart(2, "0")}</span>
            {spec.itemMultiline === true ? (
              <TextArea
                value={typeof item === "string" ? item : ""}
                style={{ minHeight: 72 }}
                onChange={(e) => {
                  const next = [...items];
                  next[i] = e.target.value;
                  write(next);
                }}
              />
            ) : (
              <Input
                type="text"
                value={typeof item === "string" ? item : ""}
                onChange={(e) => {
                  const next = [...items];
                  next[i] = e.target.value;
                  write(next);
                }}
              />
            )}
            <ListRowActions
              onRemove={() => write(items.filter((_, j) => j !== i))}
              onUp={() => {
                if (i === 0) return;
                const next = [...items];
                const tmp = next[i - 1] as unknown;
                next[i - 1] = next[i];
                next[i] = tmp;
                write(next);
              }}
              onDown={() => {
                if (i === items.length - 1) return;
                const next = [...items];
                const tmp = next[i + 1] as unknown;
                next[i + 1] = next[i];
                next[i] = tmp;
                write(next);
              }}
            />
          </div>
        ))}
      </div>
      <div style={{ marginTop: 8 }}>
        <Btn variant="ghost" small onClick={() => write([...items, ""])}>
          ADD {spec.itemLabel}
        </Btn>
      </div>
    </div>
  );
}

function ObjectListField({
  spec,
  ctx,
  path,
  mediaIds,
}: {
  spec: Extract<FieldSpec, { kind: "objectlist" }>;
  ctx: EditorApi;
  path: Path;
  mediaIds: string[];
}): ReactElement {
  const raw = getAt(ctx.draft, path);
  const items = Array.isArray(raw) ? (raw as Record<string, unknown>[]) : [];
  const write = (next: Record<string, unknown>[]): void => ctx.mutate((root) => setAt(root, path, next.length === 0 ? undefined : next));
  return (
    <div className="field">
      <span className="field-label">{spec.label}</span>
      {items.map((item, i) => (
        <div className="block" key={i}>
          <div className="block-head">
            <span className="block-index">{String(i + 1).padStart(2, "0")}</span>
            <span className="block-type">{spec.titleOf(item)}</span>
            <ListRowActions
              onRemove={() => write(items.filter((_, j) => j !== i))}
              onUp={() => {
                if (i === 0) return;
                const next = [...items];
                const tmp = next[i - 1] as Record<string, unknown>;
                next[i - 1] = next[i];
                next[i] = tmp;
                write(next);
              }}
              onDown={() => {
                if (i === items.length - 1) return;
                const next = [...items];
                const tmp = next[i + 1] as Record<string, unknown>;
                next[i + 1] = next[i];
                next[i] = tmp;
                write(next);
              }}
            />
          </div>
          <div className="block-body">
            <div className="form-row">
              {spec.fields.map((field) => (
                <FieldRenderer key={field.key} spec={field} ctx={ctx} path={[...path, i, field.key]} mediaIds={mediaIds} />
              ))}
            </div>
          </div>
        </div>
      ))}
      <Btn variant="ghost" small onClick={() => write([...items, {}])}>
        ADD {spec.itemLabel}
      </Btn>
    </div>
  );
}

function GroupField({
  spec,
  ctx,
  path,
  mediaIds,
}: {
  spec: Extract<FieldSpec, { kind: "group" }>;
  ctx: EditorApi;
  path: Path;
  mediaIds: string[];
}): ReactElement {
  return (
    <fieldset className="block" style={{ border: 0, padding: 0, margin: "0 0 16px" }}>
      <div className="panel-head" style={{ paddingLeft: 0, paddingRight: 0 }}>
        <h2>{spec.label}</h2>
      </div>
      <div className="form-row">
        {spec.fields.map((field) => (
          <FieldRenderer key={field.key} spec={field} ctx={ctx} path={[...path, field.key]} mediaIds={mediaIds} />
        ))}
      </div>
    </fieldset>
  );
}

// ---------------------------------------------------------------- sections blocks

function MediaThumb({ id }: { id: string }): ReactElement | null {
  const trimmed = id.trim();
  if (trimmed === "") return null;
  return (
    <img
      src={`/media/${encodeURIComponent(trimmed)}/400.webp`}
      alt=""
      loading="lazy"
      onError={(e) => {
        (e.target as HTMLImageElement).style.visibility = "hidden";
      }}
      onLoad={(e) => {
        (e.target as HTMLImageElement).style.visibility = "visible";
      }}
    />
  );
}

function PhotosBlock({ ctx, path, mediaIds }: { ctx: EditorApi; path: Path; mediaIds: string[] }): ReactElement {
  const raw = getAt(ctx.draft, [...path, "items"]);
  const items = Array.isArray(raw) ? (raw as PhotoItem[]) : [];
  const write = (next: PhotoItem[]): void => ctx.mutate((root) => setAt(root, [...path, "items"], next.length === 0 ? undefined : next));
  return (
    <div>
      <div className="photo-grid-edit">
        {items.map((item, i) => (
          <div className="photo-edit-card" key={i}>
            <MediaThumb id={item.image} />
            <Input
              type="text"
              className="input input-mono"
              list="ari-media-ids"
              placeholder="media id"
              aria-label={`PHOTO ${String(i + 1)} MEDIA ID`}
              value={item.image ?? ""}
              onChange={(e) => {
                const next = [...items];
                next[i] = { ...item, image: e.target.value };
                write(next);
              }}
            />
            <Input
              type="text"
              placeholder="alt"
              aria-label={`PHOTO ${String(i + 1)} ALT`}
              value={item.alt ?? ""}
              onChange={(e) => {
                const next = [...items];
                next[i] = { ...item, alt: e.target.value === "" ? undefined : e.target.value };
                write(next);
              }}
            />
            <Input
              type="text"
              placeholder="caption"
              aria-label={`PHOTO ${String(i + 1)} CAPTION`}
              value={item.caption ?? ""}
              onChange={(e) => {
                const next = [...items];
                next[i] = { ...item, caption: e.target.value === "" ? undefined : e.target.value };
                write(next);
              }}
            />
            <Btn variant="ghost" small onClick={() => write(items.filter((_, j) => j !== i))}>
              REMOVE PHOTO
            </Btn>
          </div>
        ))}
      </div>
      <div style={{ marginTop: 8 }}>
        <Btn variant="ghost" small onClick={() => write([...items, { image: "" }])}>
          ADD PHOTO
        </Btn>
      </div>
      {mediaIds.length > 0 ? (
        <datalist id="ari-media-ids">
          {mediaIds.map((id) => (
            <option key={id} value={id} />
          ))}
        </datalist>
      ) : null}
    </div>
  );
}

function SectionBlockEditor({
  block,
  index,
  total,
  ctx,
  path,
  mediaIds,
  onRemove,
  onMove,
}: {
  block: SectionBlock;
  index: number;
  total: number;
  ctx: EditorApi;
  path: Path;
  mediaIds: string[];
  onRemove: () => void;
  onMove: (delta: number) => void;
}): ReactElement {
  return (
    <div className="block">
      <div className="block-head">
        <span className="block-index">{String(index + 1).padStart(2, "0")}</span>
        <span className="block-type">{block.type}</span>
        <div className="row-actions">
          <Btn variant="ghost" small disabled={index === 0} onClick={() => onMove(-1)} aria-label="MOVE SECTION UP" title="MOVE UP">
            ↑
          </Btn>
          <Btn variant="ghost" small disabled={index === total - 1} onClick={() => onMove(1)} aria-label="MOVE SECTION DOWN" title="MOVE DOWN">
            ↓
          </Btn>
          <Btn variant="ghost" small onClick={onRemove} aria-label="REMOVE SECTION" title="REMOVE">
            REMOVE
          </Btn>
        </div>
      </div>
      <div className="block-body">
        {block.type === "text" ? (
          <div className="form-row">
            <Field label="HEADING (OPTIONAL)">
              <Input
                type="text"
                value={block.heading ?? ""}
                onChange={(e) => ctx.mutate((root) => setAt(root, [...path, "heading"], e.target.value === "" ? undefined : e.target.value))}
              />
            </Field>
            <Field label="HTML" hint="Cleaned on save: p br strong em i u ul ol li h2 h3 blockquote a. Script and style are dropped.">
              <TextArea
                className="input input-mono"
                style={{ minHeight: 160 }}
                value={block.html ?? ""}
                onChange={(e) => ctx.mutate((root) => setAt(root, [...path, "html"], e.target.value === "" ? undefined : e.target.value))}
              />
            </Field>
          </div>
        ) : block.type === "photos" ? (
          <PhotosBlock ctx={ctx} path={path} mediaIds={mediaIds} />
        ) : (
          <div className="form-row">
            <Field label="LABEL">
              <Input
                type="text"
                value={block.label ?? ""}
                onChange={(e) => ctx.mutate((root) => setAt(root, [...path, "label"], e.target.value))}
              />
            </Field>
            <Field label="URL" hint="https or mailto only">
              <Input
                type="text"
                className="input-mono"
                value={block.url ?? ""}
                onChange={(e) => ctx.mutate((root) => setAt(root, [...path, "url"], e.target.value))}
              />
            </Field>
          </div>
        )}
      </div>
    </div>
  );
}

function BlocksField({ spec, ctx, path, mediaIds }: { spec: Extract<FieldSpec, { kind: "blocks" }>; ctx: EditorApi; path: Path; mediaIds: string[] }): ReactElement {
  const raw = getAt(ctx.draft, path);
  const blocks = Array.isArray(raw) ? (raw as SectionBlock[]) : [];
  const write = (next: SectionBlock[]): void => ctx.mutate((root) => setAt(root, path, next.length === 0 ? undefined : next));
  const move = (index: number, delta: number): void => {
    const target = index + delta;
    if (target < 0 || target >= blocks.length) return;
    const next = [...blocks];
    const tmp = next[target] as SectionBlock;
    next[target] = next[index];
    next[index] = tmp;
    write(next);
  };
  return (
    <div className="field">
      <span className="field-label">{spec.label}</span>
      {blocks.map((block, i) => (
        <SectionBlockEditor
          key={i}
          block={block}
          index={i}
          total={blocks.length}
          ctx={ctx}
          path={[...path, i]}
          mediaIds={mediaIds}
          onRemove={() => write(blocks.filter((_, j) => j !== i))}
          onMove={(delta) => move(i, delta)}
        />
      ))}
      <div className="row-actions">
        <Btn variant="ghost" small onClick={() => write([...blocks, { type: "text", html: "" }])}>
          ADD TEXT
        </Btn>
        <Btn variant="ghost" small onClick={() => write([...blocks, { type: "photos", items: [{ image: "" }] }])}>
          ADD PHOTOS
        </Btn>
        <Btn variant="ghost" small onClick={() => write([...blocks, { type: "link", label: "", url: "" }])}>
          ADD LINK
        </Btn>
      </div>
    </div>
  );
}

function FieldRenderer({ spec, ctx, path, mediaIds }: { spec: FieldSpec; ctx: EditorApi; path: Path; mediaIds: string[] }): ReactElement {
  switch (spec.kind) {
    case "text":
      return <TextField spec={spec} ctx={ctx} path={path} />;
    case "check":
      return <CheckField spec={spec} ctx={ctx} path={path} />;
    case "select":
      return <SelectField spec={spec} ctx={ctx} path={path} />;
    case "group":
      return <GroupField spec={spec} ctx={ctx} path={path} mediaIds={mediaIds} />;
    case "stringlist":
      return <StringListField spec={spec} ctx={ctx} path={path} />;
    case "objectlist":
      return <ObjectListField spec={spec} ctx={ctx} path={path} mediaIds={mediaIds} />;
    case "blocks":
      return <BlocksField spec={spec} ctx={ctx} path={path} mediaIds={mediaIds} />;
  }
}

// ---------------------------------------------------------------- the screen

function validKey(key: string): boolean {
  return key === "site" || key === "page:home" || /^work:[a-z0-9][a-z0-9-]*$/.test(key);
}

export function ContentEditScreen({ docKey }: { docKey: string }): ReactElement {
  const { can } = useAuth();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [meta, setMeta] = useState<{ updatedAt: number; updatedBy: string; isNew: boolean } | null>(null);
  const [listItem, setListItem] = useState<ContentListItem | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [actionError, setActionError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState("");
  const [previewUrl, setPreviewUrl] = useState("");
  const [showRevisions, setShowRevisions] = useState(false);
  const revisions = useAsync<RevisionsResponse["revisions"]>(
    async () => (showRevisions ? api<RevisionsResponse>("GET", `/api/admin/content/${encodeURIComponent(docKey)}/revisions`).then(r => r.revisions) : []),
    [showRevisions, docKey],
  );

  const load = useCallback(async (): Promise<void> => {
    setLoadError(null);
    try {
      const [draftRes, list] = await Promise.all([
        api<DraftResponse>("GET", `/api/admin/content/${encodeURIComponent(docKey)}/draft`),
        api<ContentList>("GET", "/api/admin/content").then(r => r.documents),
      ]);
      const value = (draftRes.draft ?? {}) as Draft;
      setDraft(value);
      setMeta({ updatedAt: Date.now(), updatedBy: "", isNew: false });
      setListItem(list.find((d) => d.key === docKey) ?? null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        // A work document that does not exist yet: start from empty, created on first save.
        setDraft({});
        setMeta({ updatedAt: Date.now(), updatedBy: "", isNew: true });
        setListItem(null);
        return;
      }
      setLoadError(err instanceof ApiError ? err.message : "Could not load the document.");
    }
  }, [docKey]);

  const mediaState = useAsync<MediaItem[]>(
    async () => {
      try {
        return await api<MediaItem[]>("GET", "/api/admin/media");
      } catch {
        return [];
      }
    },
    [docKey],
  );

  const [loadedKey, setLoadedKey] = useState("");
  useEffect(() => {
    if (loadedKey === docKey) return;
    setLoadedKey(docKey);
    setDraft(null);
    setPreviewUrl("");
    setShowRevisions(false);
    void load();
  }, [docKey, loadedKey, load]);

  const mutate = useCallback((fn: (root: Draft) => void): void => {
    setDraft((current) => {
      if (current === null) return current;
      const next = cloneDraft(current);
      fn(next);
      return next;
    });
  }, []);

  const ctx = useMemo<EditorApi>(() => ({ draft: draft ?? {}, mutate }), [draft, mutate]);
  const mediaIds = mediaState.data !== null ? mediaState.data.map((m) => m.id) : [];

  if (!validKey(docKey)) {
    return (
      <div className="notfound">
        <h1>UNKNOWN DOCUMENT</h1>
        <p className="td-dim">
          <Link to="content">BACK TO CONTENT</Link>
        </p>
      </div>
    );
  }

  if (loadError !== null) {
    return (
      <>
        <div className="screen-head">
          <h1>{docKey}</h1>
        </div>
        <div className="failure" role="alert">
          <p className="failure-title">FAILED TO LOAD</p>
          <p className="failure-reason">{loadError}</p>
          <Btn variant="ghost" onClick={() => void load()}>
            RETRY
          </Btn>
        </div>
      </>
    );
  }

  if (draft === null || meta === null) {
    return (
      <>
        <div className="screen-head">
          <h1>{docKey}</h1>
        </div>
        <div className="state-block" role="status">
          <span className="spinner" aria-hidden="true" />
          <span className="mono-caps">LOADING</span>
        </div>
      </>
    );
  }

  const run = async (label: string, fn: () => Promise<void>): Promise<void> => {
    setBusy(label);
    setActionError("");
    setNotice("");
    try {
      await fn();
    } catch (err) {
      setActionError(err instanceof ApiError ? (err.code !== "" && err.code !== "unknown" ? `${err.message} (${err.code})` : err.message) : "The request failed.");
    } finally {
      setBusy("");
    }
  };

  const saveDraft = (): Promise<void> =>
    run("save", async () => {
      await api("PUT", `/api/admin/content/${encodeURIComponent(docKey)}/draft`, draft as ContentValue);
      setMeta((m) => (m === null ? m : { ...m, updatedAt: Date.now(), updatedBy: "you", isNew: false }));
      setListItem((li) => (li === null ? li : { ...li, draftDirty: true }));
      setNotice("Draft saved.");
    });

  const publish = (): Promise<void> =>
    run("publish", async () => {
      await api("POST", `/api/admin/content/${encodeURIComponent(docKey)}/publish`, draft as ContentValue);
      await load();
      setNotice("Published. The site was rebuilt.");
    });

  const unpublish = (): Promise<void> =>
    run("unpublish", async () => {
      await api("POST", `/api/admin/content/${encodeURIComponent(docKey)}/unpublish`);
      await load();
      setNotice("Unpublished.");
    });

  const makePreview = (): Promise<void> =>
    run("preview", async () => {
      const res = await api<{ url: string }>("POST", `/api/admin/content/${encodeURIComponent(docKey)}/preview`);
      setPreviewUrl(res.url);
    });

  const rollback = (version: number): Promise<void> =>
    run("rollback", async () => {
      await api("POST", `/api/admin/content/${encodeURIComponent(docKey)}/rollback/${String(version)}`);
      await load();
      setNotice(`Rolled back to version ${String(version)} (saved as a new version).`);
    });

  const schema = schemaFor(docKey);
  const published = listItem?.hasPublished ?? false;

  return (
    <>
      <div className="screen-head">
        <div>
          <h1>{docKey}</h1>
          <p className="screen-sub">
            {meta.isNew
              ? "NEW DOCUMENT — SAVED ON FIRST DRAFT SAVE"
              : `DRAFT UPDATED ${fmtDate(meta.updatedAt)}${meta.updatedBy !== "" ? ` BY ${meta.updatedBy}` : ""}`}
          </p>
        </div>
        <div className="doc-toolbar">
          {published ? <Pill tone="ok">LIVE</Pill> : <Pill tone="dim">UNPUBLISHED</Pill>}
          {listItem?.draftDiffers === true || meta.isNew ? <Pill tone="warn">DRAFT</Pill> : null}
        </div>
      </div>

      {actionError !== "" ? (
        <p className="form-error" role="alert">
          {actionError}
        </p>
      ) : null}
      {notice !== "" ? (
        <p className="form-ok" role="status">
          {notice}
        </p>
      ) : null}

      <Panel
        label="ACTIONS"
        actions={
          <Link to="content" className="btn btn-ghost btn-small">
            ALL DOCUMENTS
          </Link>
        }
      >
        <div className="row-actions">
          {can("content.edit") ? (
            <Btn variant="primary" busy={busy === "save"} disabled={busy !== ""} onClick={() => void saveDraft()}>
              SAVE DRAFT
            </Btn>
          ) : null}
          {can("content.edit") ? (
            <Btn variant="ghost" busy={busy === "preview"} disabled={busy !== ""} onClick={() => void makePreview()}>
              PREVIEW
            </Btn>
          ) : null}
          {can("content.publish") && published === null ? (
            <ConfirmBtn
              variant="primary"
              question={`PUBLISH ${docKey}?`}
              confirmLabel="PUBLISH"
              busy={busy === "publish"}
              disabled={busy !== ""}
              onConfirm={() => publish()}
            >
              PUBLISH
            </ConfirmBtn>
          ) : null}
          {can("content.publish") && published !== null ? (
            <ConfirmBtn
              variant="danger"
              question={`UNPUBLISH ${docKey}? The page goes away from the live site.`}
              confirmLabel="UNPUBLISH"
              busy={busy === "unpublish"}
              disabled={busy !== ""}
              onConfirm={() => unpublish()}
            >
              UNPUBLISH
            </ConfirmBtn>
          ) : null}
          <Btn variant="ghost" disabled={busy !== ""} aria-expanded={showRevisions} onClick={() => setShowRevisions((v) => !v)}>
            REVISIONS
          </Btn>
        </div>
        {previewUrl !== "" ? (
          <p style={{ margin: "12px 0 0" }}>
            Preview link (1 hour, noindex, no-store):{" "}
            <a href={previewUrl} target="_blank" rel="noreferrer noopener">
              open public preview
            </a>
          </p>
        ) : null}
      </Panel>

      {showRevisions ? (
        <Panel label="REVISIONS" actions={<Btn variant="ghost" small onClick={() => revisions.reload()}>REFRESH</Btn>}>
          {revisions.loading ? (
            <div className="state-block" role="status">
              <span className="spinner" aria-hidden="true" />
              <span className="mono-caps">LOADING</span>
            </div>
          ) : revisions.error !== null ? (
            <p className="form-error" role="alert">
              {errorText(revisions.error)}
            </p>
          ) : (revisions.data ?? []).length === 0 ? (
            <p className="empty">No published revisions yet.</p>
          ) : (
            <TableWrap>
              <thead>
                <tr>
                  <th>Version</th>
                  <th>Published</th>
                  <th>By</th>
                  <th>Note</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {(revisions.data ?? []).map((rev) => (
                  <tr key={rev.version}>
                    <td className="td-mono">{String(rev.version)}</td>
                    <td className="td-mono">{fmtDate(rev.publishedAt)}</td>
                    <td className="td-dim">{rev.author ?? "—"}</td>
                    <td className="td-dim td-wrap">{rev.note ?? "—"}</td>
                    <td>
                      {can("content.publish") ? (
                        <ConfirmBtn
                          variant="ghost"
                          small
                          question={`ROLL BACK to version ${String(rev.version)}? Rollback saves the old content as a new version; history is never rewritten.`}
                          confirmLabel="ROLL BACK"
                          disabled={busy !== ""}
                          onConfirm={() => rollback(rev.version)}
                        >
                          ROLL BACK
                        </ConfirmBtn>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </TableWrap>
          )}
        </Panel>
      ) : null}

      <Panel label="DOCUMENT">{schema.map((field) => <FieldRenderer key={field.key} spec={field} ctx={ctx} path={[field.key]} mediaIds={mediaIds} />)}</Panel>
    </>
  );
}
