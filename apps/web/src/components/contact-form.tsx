"use client";

import { useRef, useState } from "react";

type Fields = "name" | "email" | "body";
type FieldErrors = Partial<Record<Fields, string>>;

// Contact form: POST /api/contact with the hidden _gotcha field. Field errors
// come back in the server's `fields` object; the first failed field is focused.

export function ContactForm() {
  const [pending, setPending] = useState(false);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState("");
  const nameRef = useRef<HTMLInputElement>(null);
  const emailRef = useRef<HTMLInputElement>(null);
  const bodyRef = useRef<HTMLTextAreaElement>(null);

  async function onSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (pending) return;
    const form = event.currentTarget;
    const data = new FormData(form);
    setPending(true);
    setErrors({});
    setFormError("");
    try {
      const res = await fetch("/api/contact", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          name: data.get("name"),
          email: data.get("email"),
          body: data.get("body"),
          _gotcha: data.get("_gotcha"),
        }),
      });
      if (res.ok) {
        window.location.href = "/contact/sent";
        return;
      }
      if (res.status === 400 || res.status === 422) {
        const payload = (await res.json().catch(() => null)) as
          | { error?: string; fields?: FieldErrors }
          | null;
        const fields = payload?.fields;
        if (fields && Object.values(fields).some(Boolean)) {
          setErrors(fields);
          const first: Fields | undefined = (["name", "email", "body"] as Fields[]).find(
            (key) => fields[key]
          );
          if (first === "name") nameRef.current?.focus();
          else if (first === "email") emailRef.current?.focus();
          else if (first === "body") bodyRef.current?.focus();
        } else {
          setFormError(payload?.error ?? "Check the fields and try again.");
        }
      } else if (res.status === 429) {
        setFormError("Too many messages. Wait a minute and try again.");
      } else {
        setFormError("Something went wrong. Try again.");
      }
    } catch {
      setFormError("Network error. Try again.");
    }
    setPending(false);
  }

  const labelClass =
    "block font-mono text-[11px] uppercase tracking-[0.08em] text-muted";
  const inputClass =
    "mt-1.5 h-11 w-full border border-line-strong bg-raised px-3 font-sans text-bone placeholder:text-muted focus:border-accent";

  return (
    <form onSubmit={onSubmit} noValidate data-track="CONTACT FORM">
      <div className="absolute -left-[9999px]" aria-hidden="true">
        <label htmlFor="contact-gotcha">Leave this field empty</label>
        <input
          id="contact-gotcha"
          name="_gotcha"
          type="text"
          tabIndex={-1}
          autoComplete="off"
        />
      </div>
      <div className="space-y-4">
        <div>
          <label htmlFor="contact-name" className={labelClass}>
            Name
          </label>
          <input
            ref={nameRef}
            id="contact-name"
            name="name"
            type="text"
            required
            autoComplete="name"
            placeholder="Your name"
            aria-invalid={Boolean(errors.name)}
            aria-describedby={errors.name ? "contact-name-error" : undefined}
            className={inputClass}
          />
          {errors.name && (
            <p id="contact-name-error" className="mt-1 font-mono text-[11px] text-danger">
              {errors.name}
            </p>
          )}
        </div>
        <div>
          <label htmlFor="contact-email" className={labelClass}>
            Email
          </label>
          <input
            ref={emailRef}
            id="contact-email"
            name="email"
            type="email"
            required
            autoComplete="email"
            placeholder="you@example.com"
            aria-invalid={Boolean(errors.email)}
            aria-describedby={errors.email ? "contact-email-error" : undefined}
            className={inputClass}
          />
          {errors.email && (
            <p id="contact-email-error" className="mt-1 font-mono text-[11px] text-danger">
              {errors.email}
            </p>
          )}
        </div>
        <div>
          <label htmlFor="contact-body" className={labelClass}>
            Message
          </label>
          <textarea
            ref={bodyRef}
            id="contact-body"
            name="body"
            required
            rows={5}
            placeholder="What is this about?"
            aria-invalid={Boolean(errors.body)}
            aria-describedby={errors.body ? "contact-body-error" : undefined}
            className={`${inputClass} h-auto py-2.5`}
          />
          {errors.body && (
            <p id="contact-body-error" className="mt-1 font-mono text-[11px] text-danger">
              {errors.body}
            </p>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-4">
          <button
            type="submit"
            disabled={pending}
            className="h-11 bg-accent px-6 font-mono text-xs font-bold uppercase tracking-[0.08em] text-accent-ink hover:bg-accent-bright disabled:opacity-50"
          >
            {pending ? "Sending" : "Send \u2192"}
          </button>
          {formError && (
            <p role="alert" className="font-mono text-[11px] text-danger">
              {formError}
            </p>
          )}
        </div>
      </div>
    </form>
  );
}
