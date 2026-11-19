#!/usr/bin/env bash
set -Eeuo pipefail

# Fail closed if this kubeconfig points outside the dedicated LOCAL K3s.
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/guard-local-context.sh"
exec kubectl -n taskforge-k3s-lab port-forward --address 127.0.0.1 svc/local-gateway "${TF_LOCAL_PORT:-18080}":8080
