#!/usr/bin/env bash
set -Eeuo pipefail
source "$(dirname "$0")/common.sh"
cluster_need_env
cluster_need_config
mode="${1:-healthy}"
timeout="${2:-600}"
case "$mode" in primary|replica|healthy) ;; *) cluster_die "usage: ./cluster/wait-patroni.sh primary|replica|healthy [timeout-seconds]";; esac
case "$timeout" in ''|*[!0-9]*) cluster_die "timeout must be an integer";; esac
cluster_render >/dev/null
node_json="$(clusterctl node-info)"
ip="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["wireguard"]["ip"])')"
port="$(printf '%s' "$node_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["postgres"]["patroni_rest_port"])')"
case "$mode" in
  primary) path=/primary ;;
  replica) path=/replica ;;
  healthy) path=/health ;;
esac
password="$(cluster_derived_secret taskforge-patroni-rest-v2)"
cluster_wait_http "http://$ip:$port$path" 200 "$timeout" taskforge_patroni "$password"
echo "Patroni node $(cluster_node_id) is $mode."
