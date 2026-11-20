# Launch checklist — what needs a person

Ordered; mark done with a date.

- [ ] Create a classic GitHub token with only `read:packages` (bot account ideal);
      the registry refuses fine-grained tokens.
- [ ] Homelab: allow docker's published ports through the firewall if firewalld
      runs there (`firewall-cmd --permanent --zone=trusted --add-interface=docker0`
      plus the compose network interface, or the equivalent nft rule).
- [ ] Homelab: `/opt/aricummings/deploy` per deploy/README.md (login, key, .env,
      compose up, smoke, owner CLI, systemd timer, nginx from proxy-example.conf).
- [ ] DNS: A/AAAA records for www.aricummings.com + aricummings.com +
      admin.aricummings.com to the homelab; arigreenecummings.com when convenient.
      Bare + arigreenecummings 301 to www at the proxy.
- [ ] TLS: certbot for all names; add HSTS on the public block.
- [ ] Set TRUSTED_PROXY_CIDRS from the compose network gateway, restart.
- [ ] First owner: `docker compose exec api dotnet Ari.dll owner contact@aricummings.com`
      → open the one-time link → set name + password → enable TOTP.
- [ ] SMTP under Settings (owner, password re-confirm) so the contact form and
      alerts deliver. Until then messages queue in the outbox only.
- [ ] Review drafted copy in Content: work:enclave, work:rustos, work:multiforge,
      work:edex, work:red-wagon-rentals, work:connected-riding. MultiForge
      framing (GPL-3.0 vs closed-source) still open in the ledger.
- [ ] Optional: GeoLite2 mmdb at deploy/.env ARI_GEODB_PATH for geo reports.
- [ ] Optional: real GitHub avatar via Media + icon swap (placeholder documented).
- [ ] Cutover: after DNS settles and the new site is verified, disable the old
      pull-loop on the desktop (systemctl unit or cron running tools/publish),
      then fast-forward main to v0.1.0 and push.
- [ ] Copy backups AND the key off the homelab; run one restore drill.
- [ ] Passkeys: verify on a real phone + security key (software tests only).
