#!/usr/bin/env bash
set -euo pipefail

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$ROOT"

printf '[browser-security] static architecture checks\n'
python3 - <<'PY'
from __future__ import annotations

import pathlib
import re
import sys
import yaml

root = pathlib.Path('.')


def die(message: str) -> None:
    raise SystemExit(f'Browser API security invariant failed: {message}')


def text(path: str) -> str:
    value = root / path
    if not value.is_file():
        die(f'missing {path}')
    return value.read_text(encoding='utf-8')

required = [
    'services/browser/api/TaskForge.Browser.Api.csproj',
    'services/browser/api/Dockerfile',
    'services/browser/api/Program.cs',
    'services/browser/api/Security/BrowserUrlPolicy.cs',
    'services/browser/api/Security/BrowserCallerResolver.cs',
    'services/browser/api/Services/BrowserPageFactory.cs',
    'services/browser/api/Services/BrowserSessionRegistry.cs',
    'services/browser/api/Infrastructure/RedisFixedWindowRateLimiter.cs',
    'services/browser/api/Infrastructure/BrowserArtifactCacheCodec.cs',
    'services/identity/api/Services/Security/IdentityAuthRateLimiter.cs',
]
for path in required:
    text(path)

contracts = text('services/browser/api/Contracts/BrowserContracts.cs')
request_records = re.findall(r'public sealed record\s+([A-Za-z0-9]+Request)\((.*?)\);', contracts, re.S)
for name, body in request_records:
    if re.search(r'\bUrl\b', body):
        die(f'{name} exposes an arbitrary Url field')

policy = text('services/browser/api/Security/BrowserUrlPolicy.cs')
for marker in (
    'NormalizeRelativePath',
    'BROWSER_API_RECURSION_BLOCKED',
    '"/api/site"',
    '"/api/browser"',
    'SameOrigin(baseUri, target)',
    'IsBrowserApiEndpoint',
):
    if marker not in policy:
        die(f'URL policy lost required marker: {marker}')
if 'BuildPageUri(Uri baseUri, string? path)' not in policy:
    die('page navigation is no longer based on a configured site plus relative path')

factory = text('services/browser/api/Services/BrowserPageFactory.cs')
if '&& !IsSafeMethod(request.Method)' not in factory:
    die('read-only sessions no longer block unsafe HTTP methods')
if 'uri.AbsolutePath.StartsWith("/api/"' in factory:
    die('read-only protection regressed to /api-only filtering')
if 'document", StringComparison.OrdinalIgnoreCase)' not in factory or 'SameOrigin(siteBaseUri, uri)' not in factory:
    die('cross-origin document navigation protection is missing')
if '_urlPolicy.IsBrowserApiEndpoint(uri)' not in factory:
    die('Chromium can recursively call Browser API endpoints')
if 'readOnly && uri.Scheme is "ws" or "wss"' not in factory:
    die('read-only sessions no longer block WebSockets')

sessions = text('services/browser/api/Services/BrowserSessionRegistry.cs')
for marker in (
    'AUTHENTICATION_REQUIRED_FOR_MUTATING_SESSION',
    'request.ReadOnly ?? true',
    'X-TaskForge-Browser-Session-Token',
):
    if marker not in sessions and marker != 'X-TaskForge-Browser-Session-Token':
        die(f'session security marker missing: {marker}')
caller = text('services/browser/api/Security/BrowserCallerResolver.cs')
if 'http.Request.Headers.Authorization' not in caller or 'Bearer ' not in caller:
    die('authenticated Browser API calls must use an explicit Authorization Bearer token')
if 'Request.Cookies' in caller or 'TryGetValue("tf_at"' in caller or "TryGetValue('tf_at'" in caller:
    die('Browser API must ignore ambient tf_at cookies; cookie auth would reintroduce CSRF/CORS credential risk')

program = text('services/browser/api/Program.cs')
if 'X-TaskForge-Browser-Session-Token' not in program:
    die('session token header contract is missing')
if 'AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()' not in program:
    die('public Browser API CORS policy unexpectedly changed')
if 'Production browser-api requires BrowserRateLimits:Enabled=true' not in program:
    die('production does not require application rate limiting')
if 'Production browser-api requires a Redis connection string' not in program:
    die('production does not require Redis')
if 'response.Headers.Vary = "Authorization"' not in program or 'Authorization, Cookie' in program:
    die('Browser API cache variance must match explicit Bearer-only authentication')

for path in (
    'services/browser/api/Infrastructure/RedisFixedWindowRateLimiter.cs',
    'services/identity/api/Services/Security/IdentityAuthRateLimiter.cs',
):
    value = text(path)
    if 'ScriptEvaluateAsync' not in value or "redis.call('INCR'" not in value or "redis.call('EXPIRE'" not in value:
        die(f'{path} does not use atomic Redis fixed-window increments')
    if 'StringIncrementAsync' in value or 'KeyExpireAsync' in value:
        die(f'{path} returned to non-atomic INCR/EXPIRE calls')

identity_user = text('services/identity/api/Domain/IdentityUser.cs')
identity_db = text('services/identity/api/Data/IdentityDbContext.cs')
auth_endpoints = text('services/identity/api/Endpoints/Auth/AuthEndpoints.cs')
if 'AccountType' not in identity_user or 'HasDefaultValue("human")' not in identity_db:
    die('AI account marker is not persisted with a human default')
if 'TryNormalizeAccountType(request.AccountType' not in auth_endpoints:
    die('registration does not validate accountType')

browser_csproj = text('services/browser/api/TaskForge.Browser.Api.csproj')
dockerfile = text('services/browser/api/Dockerfile')

# Swashbuckle.AspNetCore 10+ uses Microsoft.OpenApi 2+, whose model types moved
# from Microsoft.OpenApi.Models into the Microsoft.OpenApi namespace. Catch the
# stale namespace before the Docker build downloads the large Playwright image.
swashbuckle = re.search(r'Swashbuckle\.AspNetCore" Version="([0-9]+)\.', browser_csproj)
if swashbuckle and int(swashbuckle.group(1)) >= 10:
    if 'using Microsoft.OpenApi.Models;' in program:
        die('Swashbuckle 10+ requires Microsoft.OpenApi namespace; Microsoft.OpenApi.Models is obsolete')
    if 'using Microsoft.OpenApi;' not in program:
        die('Browser Program.cs must import Microsoft.OpenApi for Swashbuckle 10+')
version = re.search(r'Microsoft\.Playwright" Version="([^"]+)"', browser_csproj)
image = re.search(r'playwright/dotnet:v([0-9.]+)-', dockerfile)
if not version or not image or version.group(1) != image.group(1):
    die('Microsoft.Playwright package and runtime image versions differ')
for marker in ('USER pwuser', 'HOME=/tmp', 'PLAYWRIGHT_BROWSERS_PATH=/ms-playwright'):
    if marker not in dockerfile:
        die(f'Browser Dockerfile hardening marker missing: {marker}')

# A runtime-specific, self-contained publish with --no-restore is valid only when
# restore generated assets for the exact same RID. Otherwise publish fails with
# NETSDK1047 because project.assets.json contains net10.0 but not net10.0/linux-x64.
restore_linux_x64 = re.search(
    r'dotnet\s+restore\s+services/browser/api/TaskForge\.Browser\.Api\.csproj(?:(?!\n\s*(?:COPY|RUN|FROM)).)*?(?:-r|--runtime)\s+linux-x64',
    dockerfile,
    re.S,
)
publish_linux_x64_no_restore = re.search(
    r'dotnet\s+publish\s+services/browser/api/TaskForge\.Browser\.Api\.csproj(?:(?!\n\s*(?:COPY|RUN|FROM)).)*?(?:-r|--runtime)\s+linux-x64(?:(?!\n\s*(?:COPY|RUN|FROM)).)*?--no-restore',
    dockerfile,
    re.S,
)
if publish_linux_x64_no_restore and not restore_linux_x64:
    die('Browser Dockerfile publishes linux-x64 with --no-restore but restore does not target linux-x64')

routes = text('apps/gateway/snippets/api-routes.conf')
proxy = text('apps/gateway/snippets/proxy-common.conf')
for marker in ('^/api/(site|browser)', 'limit_req zone=tf_browser_public', 'limit_conn tf_browser_connections'):
    if marker not in routes:
        die(f'gateway Browser API protection missing: {marker}')
if 'X-TaskForge-Client-IP $remote_addr' not in proxy or 'CF-Connecting-IP ""' not in proxy:
    die('gateway does not overwrite untrusted client-IP headers')
for template in ('dev.conf', 'http.conf', 'https.conf'):
    value = text(f'apps/gateway/templates/{template}')
    if 'limit_req_zone $binary_remote_addr zone=tf_browser_public' not in value:
        die(f'{template} has no Browser API request zone')
    if 'limit_conn_zone $binary_remote_addr zone=tf_browser_connections' not in value:
        die(f'{template} has no Browser API connection zone')

for environment in ('dev', 'prod'):
    compose_path = root / f'deploy/{environment}/compose/50-integrations.yaml'
    document = yaml.safe_load(compose_path.read_text(encoding='utf-8'))
    service = (document.get('services') or {}).get('browser-api')
    if not service:
        die(f'{environment}: browser-api service is missing')
    if service.get('ports'):
        die(f'{environment}: browser-api must not publish host ports')
    if service.get('read_only') is not True:
        die(f'{environment}: browser-api root filesystem must be read-only')
    if service.get('cap_drop') != ['ALL']:
        die(f'{environment}: browser-api must drop all capabilities')
    if 'no-new-privileges:true' not in (service.get('security_opt') or []):
        die(f'{environment}: browser-api lacks no-new-privileges')
    if service.get('init') is not True:
        die(f'{environment}: browser-api must use an init process for Chromium children')
    if not service.get('tmpfs') or not service.get('shm_size'):
        die(f'{environment}: browser-api writable temp/shm limits are missing')
    for key in ('mem_limit', 'cpus', 'pids_limit'):
        if not service.get(key):
            die(f'{environment}: browser-api {key} limit is missing')
    volumes = repr(service.get('volumes') or [])
    if 'docker.sock' in volumes:
        die(f'{environment}: browser-api must never mount the Docker socket')
    environment_values = service.get('environment') or {}
    for key in ('ConnectionStrings__Redis', 'BrowserRateLimits__Enabled', 'Browser__Sites__main', 'Browser__MaxCachedArtifactBytes', 'Browser__MaxArtifactResponseBytes'):
        if key not in environment_values:
            die(f'{environment}: browser-api environment misses {key}')
    if environment == 'prod':
        image_value = str(service.get('image') or '')
        if '/browser-api:' not in image_value:
            die('prod: browser-api does not use the published browser-api image')
        if not str(environment_values.get('Browser__Sites__main', '')).startswith('${BROWSER_MAIN_ORIGIN:-https://'):
            die('prod: main browser origin does not default to HTTPS')

prod_gateway = yaml.safe_load(text('deploy/prod/compose/10-apps-gateway.yaml'))
gateway_aliases = (((prod_gateway.get('services') or {}).get('gateway') or {}).get('networks') or {}).get('default', {}).get('aliases') or []
if '${DOMAIN:?set DOMAIN}' not in gateway_aliases or '${CT_DOMAIN:?set CT_DOMAIN}' not in gateway_aliases:
    die('prod gateway must own DOMAIN and CT_DOMAIN network aliases for Chromium TLS routing')

for environment in ('dev', 'prod'):
    env_example = text(f'deploy/{environment}/.env.example')
    if 'BROWSER_MAX_ARTIFACT_RESPONSE_BYTES=' not in env_example:
        die(f'{environment}: artifact response size limit is missing from .env.example')

codec = text('services/browser/api/Infrastructure/BrowserArtifactCacheCodec.cs')
for marker in ('TFART001', 'FullPageTruncated', 'Annotated'):
    if marker not in codec:
        die(f'artifact cache codec metadata marker missing: {marker}')

print('[browser-security] architecture invariants ok')
PY
