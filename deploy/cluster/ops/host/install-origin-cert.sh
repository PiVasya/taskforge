#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"
cluster_need_root
cert="${1:-}"
key="${2:-}"
[ -s "$cert" ] && [ -s "$key" ] || cluster_die "usage: sudo ./cluster/ops/host/install-origin-cert.sh FULLCHAIN_PEM PRIVATE_KEY_PEM"
cluster_runtime_prepare
install -m 644 "$cert" "$TASKFORGE_CLUSTER_RUNTIME/tls/fullchain.pem"
install -m 600 "$key" "$TASKFORGE_CLUSTER_RUNTIME/tls/privkey.pem"
openssl x509 -in "$TASKFORGE_CLUSTER_RUNTIME/tls/fullchain.pem" -noout -subject -issuer -dates
openssl pkey -in "$TASKFORGE_CLUSTER_RUNTIME/tls/privkey.pem" -check -noout >/dev/null
echo "Origin TLS certificate installed."
