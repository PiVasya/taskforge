#!/usr/bin/env bash
set -Eeuo pipefail
# READ-ONLY Kubernetes preflight; prints NO database credentials.
command -v kubectl >/dev/null || { echo 'kubectl is required' >&2; exit 2; }
command -v python3 >/dev/null || { echo 'python3 is required' >&2; exit 2; }
NAMESPACE="${TF_K3S_NAMESPACE:-taskforge}"
kubectl -n "$NAMESPACE" get configmap taskforge-app-config >/dev/null
kubectl -n "$NAMESPACE" get secret taskforge-app-secrets >/dev/null
if [[ "${TF_K3S_SKIP_PULL_SECRET:-}" != 'yes' ]]; then
  kubectl -n "$NAMESPACE" get secret ghcr-pull >/dev/null
fi
secrets="$(kubectl -n "$NAMESPACE" get secret taskforge-db-connections -o json)"
TF_K3S_DB_SECRET_JSON="$secrets" python3 - <<'PY'
import base64,json,os,re,sys
names={
  'identity-api':'taskforge_identity','education-api':'taskforge_education',
  'content-api':'taskforge_content','tasks-api':'taskforge_tasks',
  'solutions-api':'taskforge_solutions','execution-api':'taskforge_execution',
  'ai-api':'taskforge_ai','support-api':'taskforge_support',
  'minecraft-api':'taskforge_minecraft','files-api':'taskforge_files',
  'notifications-api':'taskforge_notifications','observability-api':'taskforge_observability',
  'rating-worker':'taskforge_solutions','telegram-quiz-bot':'taskforge_telegram_quiz',
}
secret=json.loads(os.environ['TF_K3S_DB_SECRET_JSON'])
data=secret.get('data') or {}
errors=[]
for name,db in names.items():
  if name not in data:
    errors.append(name+': missing DB connection secret');continue
  try:
    value=base64.b64decode(data[name],validate=True).decode('utf-8')
    fields={}
    for part in value.split(';'):
      if '=' in part:
        key,val=part.split('=',1)
        fields[key.strip().lower().replace(' ','')]=val.strip()
    if fields.get('database')!=db:errors.append(name+': database name mismatch')
    if not fields.get('host') or fields['host'] in ('localhost','127.0.0.1'):
      errors.append(name+': Host is empty or points to loopback')
    if fields.get('port')!='5433':errors.append(name+': expected Yugabyte YSQL port 5433')
    if not fields.get('password'):errors.append(name+': password must not be empty')
    if os.environ.get('TF_K3S_PRODUCTION')=='yes':
      if fields.get('sslmode','').lower() not in ('verifyfull','verifyca'):
        errors.append(name+': production requires SSL Mode=VerifyFull/VerifyCA')
  except (ValueError,UnicodeError,KeyError) as exc:
    errors.append(name+': invalid connection string/encoding ('+type(exc).__name__+')')
if errors:
  for msg in errors:print('ERROR:',msg,file=sys.stderr)
  sys.exit(1)
print('PASS: checked all 14 DB connection secrets (no credential values printed).')
PY
