# Launch checklist — what needs a person

Status as of 2026-11-22. Live release: v0.1.5 on RWR-Web, images on `latest`,
installed by the one-minute updater.

## Done

- [x] Server, compose stack, updater timer, first owner account.
- [x] DNS and TLS: www + bare aricummings.com, admin.aricummings.com,
      nullsect0r.dev, nullsect0r.tech, arigreenecummings.com + www.
- [x] Admin paths return 404 on every public domain.
- [x] Scheduled jobs run; GitHub sync caches all repos.
- [x] Nightly backup job shipped (first run 2026-11-23 02:45 UTC, not yet observed).
- [x] `main` fast-forwarded to the rebuild. No old publish loop found on this
      desktop or RWR-Web, and no old Vite site on the proxied hosts' port 8080.
- [x] Real visitor IPs (2026-11-22): the proxy's stream listener now passes them with
      the PROXY protocol. Enclave's TLS stays end to end. Undo on the proxy VM:
      `bash /root/real-ip.sh undo`.

## Needs a person

- [ ] Off-machine backups: deferred by the owner on 2026-11-22. Backups and the key
      are only on RWR-Web until then.
- [ ] SMTP under Settings (owner, password re-confirm). Until then contact
      messages stay in the admin and outbox.
- [ ] Sign in and check the admin screens on the live site.
- [ ] Review drafted copy in Content: work:enclave, work:rustos, work:multiforge,
      work:edex, work:red-wagon-rentals, work:connected-riding.
- [ ] MultiForge framing: GPL-3.0 or closed-source.
- [ ] Optional: GeoLite2 mmdb at deploy/.env ARI_GEODB_PATH for geo reports.
- [ ] Optional: real GitHub avatar via Media + icon swap.
- [ ] Passkeys: verify on a real phone + security key (software tests only).

## Other sites on RWR-Web

- [ ] Red Wagon: restored from the 2026-11-19 11:15 UTC backup, then updated to the
      latest release with auto-updates back on. Reconcile Booqable and Stripe for the
      gap; 70 media records have no files.
- [ ] Connected Riding `cr` / `cr2`: offline. Data volumes and /opt folders are
      kept on purpose. Do not delete.
