#!/bin/sh
set -eu

DOMAIN="${DOMAIN:-taskforge.example.com}"
GATEWAY_MODE="${GATEWAY_MODE:-auto}"
TASKFORGE_NODE_ROLE="${TASKFORGE_NODE_ROLE:-primary}"
TASKFORGE_DEBUG_LOGS="${TASKFORGE_DEBUG_LOGS:-0}"

if [ "$TASKFORGE_DEBUG_LOGS" = "1" ]; then
  echo "[taskforge-debug] logs=on service=gateway mode=${GATEWAY_MODE} role=${TASKFORGE_NODE_ROLE} domain=${DOMAIN} ct=${CT_DOMAIN:-}"
else
  echo "[taskforge-debug] logs=off service=gateway"
fi

render_ha_snippet () {
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
  envsubst '${DOMAIN} ${CT_DOMAIN}' < "/etc/nginx/templates/$1" > /etc/nginx/conf.d/default.conf
  if [ "$TASKFORGE_DEBUG_LOGS" = "1" ]; then
    cat > /tmp/taskforge-debug-nginx-prefix.conf <<'EOF'
log_format taskforge_debug 'TFDBG GATEWAY request_id=$request_id remote=$remote_addr host=$host method=$request_method path="$uri" status=$status bytes=$body_bytes_sent request_time=$request_time upstream="$upstream_addr" upstream_status="$upstream_status" upstream_time="$upstream_response_time" ref="$http_referer" ua="$http_user_agent"';
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
elif [ -f "/etc/letsencrypt/live/${DOMAIN}/fullchain.pem" ]; then
  echo "[nginx] Using HTTPS config for ${DOMAIN}"
  render_conf https.conf
else
  echo "[nginx] Using BOOTSTRAP (HTTP) config for ${DOMAIN}"
  render_conf bootstrap.conf
fi
