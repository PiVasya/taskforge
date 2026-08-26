# Bundled network data

`cloudflare-ips-v4.txt` and `cloudflare-ips-v6.txt` are the official Cloudflare
proxy source ranges bundled for deterministic first-server firewall setup.
Snapshot date: 2026-08-25.

Official sources:
- https://www.cloudflare.com/ips-v4
- https://www.cloudflare.com/ips-v6

TaskForge defaults to allowing public HTTP/HTTPS only from these ranges. Set
`TASKFORGE_FIREWALL_WEB_SOURCE=any` for a deployment that intentionally accepts
direct origin traffic from the whole Internet.
