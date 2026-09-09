#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_root
cluster_need_env
command -v openssl >/dev/null 2>&1 || cluster_die "openssl is required"
out="${1:-$TASKFORGE_ROOT/taskforge-cluster-secrets.enc}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
install -m 600 "$TASKFORGE_ENV_FILE" "$tmp/env"
mkdir -p "$tmp/tls" "$tmp/code-analyzer"
[ ! -d "$TASKFORGE_CLUSTER_RUNTIME/tls" ] || cp -a "$TASKFORGE_CLUSTER_RUNTIME/tls/." "$tmp/tls/"
private="$(cluster_read_env CODE_ANALYZER_PRIVATE_KEY_PATH)"
public="$(cluster_read_env CODE_ANALYZER_PUBLIC_KEY_PATH)"
[ ! -s "$private" ] || install -m 600 "$private" "$tmp/code-analyzer/private.pem"
[ ! -s "$public" ] || install -m 644 "$public" "$tmp/code-analyzer/public.pem"
[ ! -f "$TASKFORGE_ROOT/config.json" ] || install -m 600 "$TASKFORGE_ROOT/config.json" "$tmp/registry-config.json"
cat > "$tmp/README.txt" <<'TXT'
Encrypted TaskForge cluster secrets bundle.
Contains .env, optional Cloudflare Origin CA files, code-analyzer signing keys,
and optional GHCR config.json. Never commit or upload the decrypted contents.
TXT
tar -C "$tmp" -czf - . | openssl enc -aes-256-cbc -salt -pbkdf2 -iter 250000 -out "$out"
chmod 600 "$out"
chown "$(cluster_project_owner)" "$out"
echo "Encrypted secrets bundle: $out"
