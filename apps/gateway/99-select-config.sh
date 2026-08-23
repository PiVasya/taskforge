#!/bin/sh
set -eu

DOMAIN="${DOMAIN:-taskforge.example.com}"
GATEWAY_MODE="${GATEWAY_MODE:-auto}"
TASKFORGE_NODE_ROLE="${TASKFORGE_NODE_ROLE:-primary}"
TASKFORGE_HA_DYNAMIC_READY="${TASKFORGE_HA_DYNAMIC_READY:-false}"
HA_NODE_ID="${HA_NODE_ID:-node}"
GATEWAY_TLS_CERT_FILE="${GATEWAY_TLS_CERT_FILE:-/etc/letsencrypt/live/${DOMAIN}/fullchain.pem}"
GATEWAY_TLS_KEY_FILE="${GATEWAY_TLS_KEY_FILE:-/etc/letsencrypt/live/${DOMAIN}/privkey.pem}"
TASKFORGE_DEBUG_LOGS="${TASKFORGE_DEBUG_LOGS:-0}"
BROWSER_EDGE_RATE_RPS="${BROWSER_EDGE_RATE_RPS:-25}"

case "$BROWSER_EDGE_RATE_RPS" in
  ''|*[!0-9]*) echo "[nginx] BROWSER_EDGE_RATE_RPS must be an integer" >&2; exit 1 ;;
esac
if [ "$BROWSER_EDGE_RATE_RPS" -lt 5 ] || [ "$BROWSER_EDGE_RATE_RPS" -gt 1000 ]; then
  echo "[nginx] BROWSER_EDGE_RATE_RPS must be between 5 and 1000" >&2
  exit 1
fi
export BROWSER_EDGE_RATE_RPS

if [ "$TASKFORGE_DEBUG_LOGS" = "1" ]; then
  echo "[taskforge-debug] logs=on service=gateway mode=${GATEWAY_MODE} role=${TASKFORGE_NODE_ROLE} ha_dynamic=${TASKFORGE_HA_DYNAMIC_READY} node=${HA_NODE_ID} domain=${DOMAIN} ct=${CT_DOMAIN:-}"
else
  echo "[taskforge-debug] logs=off service=gateway"
fi

render_ha_snippet () {
  case "$HA_NODE_ID" in
    *[!A-Za-z0-9_-]*|"") echo "[nginx] HA_NODE_ID contains invalid characters" >&2; exit 1 ;;
  esac

  if [ "$TASKFORGE_HA_DYNAMIC_READY" = "true" ] || [ "$TASKFORGE_HA_DYNAMIC_READY" = "1" ]; then
    cat > /etc/nginx/snippets/ha-routes.conf <<EOF
# Managed cluster mode. The host-level TaskForge cluster controller owns the marker file.
location = /ha/primary-ready {
  default_type application/json;
  add_header Cache-Control "no-store" always;
  if (-f /run/taskforge-cluster/traffic-ready) {
    return 200 '{"status":"ready","node":"${HA_NODE_ID}"}';
  }
  return 503 '{"status":"standby","node":"${HA_NODE_ID}"}';
}

location = /ha/traffic-ready {
  default_type application/json;
  add_header Cache-Control "no-store" always;
  if (-f /run/taskforge-cluster/traffic-ready) {
    return 200 '{"status":"ready","node":"${HA_NODE_ID}"}';
  }
  return 503 '{"status":"standby","node":"${HA_NODE_ID}"}';
}

location = /ha/live {
  default_type application/json;
  add_header Cache-Control "no-store" always;
  return 200 '{"status":"ok","service":"gateway","node":"${HA_NODE_ID}"}';
}
EOF
    return
  fi

  if [ "$TASKFORGE_NODE_ROLE" = "primary" ]; then
    TASKFORGE_HA_PRIMARY_READY_STATUS="200"
    TASKFORGE_HA_PRIMARY_READY_BODY='{"status":"ready","role":"primary"}'
  else
    TASKFORGE_HA_PRIMARY_READY_STATUS="503"
    TASKFORGE_HA_PRIMARY_READY_BODY="{\"status\":\"standby\",\"role\":\"${TASKFORGE_NODE_ROLE}\"}"
  fi

  export TASKFORGE_NODE_ROLE TASKFORGE_HA_PRIMARY_READY_STATUS TASKFORGE_HA_PRIMARY_READY_BODY
  envsubst '${TASKFORGE_NODE_ROLE} ${TASKFORGE_HA_PRIMARY_READY_STATUS} ${TASKFORGE_HA_PRIMARY_READY_BODY}' \
    < /etc/nginx/snippets/ha-routes.conf.tpl \
    > /etc/nginx/snippets/ha-routes.conf
}

render_conf () {
  render_ha_snippet
  export GATEWAY_TLS_CERT_FILE GATEWAY_TLS_KEY_FILE
  envsubst '${DOMAIN} ${CT_DOMAIN} ${BROWSER_EDGE_RATE_RPS} ${GATEWAY_TLS_CERT_FILE} ${GATEWAY_TLS_KEY_FILE}' < "/etc/nginx/templates/$1" > /etc/nginx/conf.d/default.conf
  if [ "$TASKFORGE_DEBUG_LOGS" = "1" ]; then
    cat > /tmp/taskforge-debug-nginx-prefix.conf <<'EOF'
log_format taskforge_debug 'TFDBG GATEWAY request_id=$request_id remote=$remote_addr host=$host method=$request_method path="$uri" status=$status bytes=$body_bytes_sent request_time=$request_time upstream="$upstream_addr" upstream_status="$upstream_status" upstream_time="$upstream_response_time" ua="$http_user_agent"';
access_log /var/log/nginx/access.log taskforge_debug;
error_log /var/log/nginx/error.log info;
EOF
    cat /tmp/taskforge-debug-nginx-prefix.conf /etc/nginx/conf.d/default.conf > /tmp/taskforge-debug-default.conf
    mv /tmp/taskforge-debug-default.conf /etc/nginx/conf.d/default.conf
  fi
}

if [ "$GATEWAY_MODE" = "dev" ]; then
  echo "[nginx] Using DEV microservices routing"
  render_conf dev.conf
elif [ "$GATEWAY_MODE" = "http" ]; then
  echo "[nginx] Using HTTP production microservices routing"
  render_conf http.conf
elif [ "$GATEWAY_MODE" = "https" ]; then
  echo "[nginx] Using HTTPS production microservices routing"
  render_conf https.conf
elif [ -f "$GATEWAY_TLS_CERT_FILE" ] && [ -f "$GATEWAY_TLS_KEY_FILE" ]; then
  echo "[nginx] Using HTTPS config for ${DOMAIN}"
  render_conf https.conf
else
  echo "[nginx] Using BOOTSTRAP (HTTP) config for ${DOMAIN}"
  render_conf bootstrap.conf
fi
