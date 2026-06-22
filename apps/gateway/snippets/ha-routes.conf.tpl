# Public HA endpoint for Cloudflare health checks / failover monitors.
# This endpoint answers the question: "is this node allowed to receive live traffic?"
# It intentionally depends on TASKFORGE_NODE_ROLE, not just on nginx liveness.
location = /ha/primary-ready {
  default_type application/json;
  add_header Cache-Control "no-store" always;
  return ${TASKFORGE_HA_PRIMARY_READY_STATUS} '${TASKFORGE_HA_PRIMARY_READY_BODY}';
}

location = /ha/live {
  default_type application/json;
  add_header Cache-Control "no-store" always;
  return 200 '{"status":"ok","service":"gateway","role":"${TASKFORGE_NODE_ROLE}"}';
}
