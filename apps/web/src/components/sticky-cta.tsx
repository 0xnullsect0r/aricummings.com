"use client";

import { useEffect, useState } from "react";

// Phone-only contact bar. It stays out of the way: hidden over the hero, which
// has its own buttons, and once the contact form or footer is on screen.

export function StickyCta() {
  const [show, setShow] = useState(false);

  useEffect(() => {
    const update = () => {
      const contact = document.getElementById("contact");
      const reachedContact = contact ? contact.getBoundingClientRect().top < window.innerHeight : false;
      setShow(window.scrollY > window.innerHeight * 0.75 && !reachedContact);
    };
    update();
    window.addEventListener("scroll", update, { passive: true });
    window.addEventListener("resize", update);
    return () => {
      window.removeEventListener("scroll", update);
      window.removeEventListener("resize", update);
    };
  }, []);

  if (!show) return null;
  return (
    <div className="fixed inset-x-0 bottom-0 z-40 md:hidden" data-track="STICKY CTA">
      <a
        href="#contact"
        className="flex h-12 items-center justify-center border-t border-edge bg-accent font-mono text-xs font-bold uppercase tracking-[0.08em] text-accent-ink"
      >
        Contact &#8594;
      </a>
    </div>
  );
}
