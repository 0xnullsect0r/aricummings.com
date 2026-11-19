import type { MediaAsset } from "@/lib/content";

// No next/image (read-only filesystem, no optimizer; the media pipeline
// already produced every size). <picture> AVIF + WebP with srcSet from the
// asset's widths, real width/height, lazy by default.

export function MediaImage({
  id,
  media,
  alt = "",
  caption,
  sizes = "(min-width: 48rem) 50vw, 100vw",
  eager = false,
}: {
  id?: string;
  media: Record<string, MediaAsset> | undefined;
  alt?: string;
  caption?: string;
  sizes?: string;
  eager?: boolean;
}) {
  if (!id) return null;
  const asset = media?.[id];
  if (
    !asset ||
    !Array.isArray(asset.widths) ||
    asset.widths.length === 0 ||
    typeof asset.w !== "number" ||
    typeof asset.h !== "number"
  ) {
    return null;
  }
  const widths = asset.widths;
  const srcSet = (ext: string) =>
    widths.map((width) => `/media/${id}/${width}.${ext} ${width}w`).join(", ");
  const fallback = widths[widths.length - 1];
  return (
    <figure className="bg-ground">
      <picture>
        <source type="image/avif" srcSet={srcSet("avif")} sizes={sizes} />
        <source type="image/webp" srcSet={srcSet("webp")} sizes={sizes} />
        <img
          src={`/media/${id}/${fallback}.webp`}
          width={asset.w}
          height={asset.h}
          alt={alt}
          loading={eager ? "eager" : "lazy"}
          decoding="async"
          className="block h-auto w-full"
        />
      </picture>
      {caption && (
        <figcaption className="border-t border-line px-3 py-2 font-mono text-[11px] text-muted">
          {caption}
        </figcaption>
      )}
    </figure>
  );
}
