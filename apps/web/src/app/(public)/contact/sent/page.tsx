import type { Metadata } from "next";
import { ConversionPing } from "@/components/conversion-ping";

export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  title: "Message sent",
  robots: { index: false, follow: false },
};

// Contact success page. Emits the conversion beacon event on mount.
export default function ContactSentPage() {
  return (
    <section data-track="MESSAGE SENT" className="mx-auto max-w-[72rem] px-4 py-24 md:px-6">
      <p className="font-mono text-[11px] uppercase tracking-[0.08em] text-muted md:text-xs">
        CONTACT
      </p>
      <h1 className="mt-3 font-mono text-[clamp(2.75rem,7vw,5.5rem)] font-bold uppercase leading-[0.95] text-bone">
        MESSAGE SENT
      </h1>
      <p className="mt-6 max-w-[65ch] text-bone-dim">
        Your message was received and queued. You will hear back at the address you sent from.
      </p>
      <a
        href="/"
        className="mt-8 inline-flex min-h-[44px] items-center font-mono text-xs uppercase tracking-[0.08em] text-accent-bright hover:underline"
      >
        &#8592; Back to index
      </a>
      <ConversionPing />
    </section>
  );
}
