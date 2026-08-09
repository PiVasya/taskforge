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
    'services/browser/api/Security/BrowserSessionAccessPolicy.cs',
    'services/browser/api/Services/BrowserPageFactory.cs',
    'services/browser/api/Services/BrowserSessionRegistry.cs',
    'services/browser/api/Infrastructure/RedisFixedWindowRateLimiter.cs',
    'services/browser/api/Infrastructure/BrowserArtifactCacheCodec.cs',
    'services/browser/api/Services/AgentAccessService.cs',
    'services/browser/api/Services/PublicAgentArtifactStore.cs',
    'services/browser/api/Services/AiAccessTelemetryReporter.cs',
    'services/bots/support-bot/AiAccessTelemetry.cs',
    'services/identity/api/Services/Telemetry/AiAccessTelemetryClient.cs',
    'tools/browser-url-policy-check/TaskForge.Browser.UrlPolicyCheck.csproj',
    'tools/browser-url-policy-check/Program.cs',
    'tools/browser-session-access-check/TaskForge.Browser.SessionAccessCheck.csproj',
    'tools/browser-session-access-check/Program.cs',
    'apps/gateway/snippets/cloudflare-real-ip.conf',
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
if 'Uri.TryCreate(value, UriKind.Absolute' in policy:
    die('relative Browser path validation must not use Uri.TryCreate(... Absolute); .NET treats rooted paths such as / as absolute URI forms')
for marker in ('HasUriScheme', 'decodedPath.StartsWith("//"', 'segments.Any(x => x is "." or "..")'):
    if marker not in policy:
        die(f'URL policy lost decoded-path hardening marker: {marker}')

policy_check = text('tools/browser-url-policy-check/Program.cs')
for marker in ('("/", "/")', '"https://evil.example/"', '"/%252e%252e/secret"', '"/api/site/render"'):
    if marker not in policy_check:
        die(f'Browser URL policy regression coverage missing: {marker}')

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
    'OwnerIsAuthenticated = caller.IsAuthenticated',
    'BrowserSessionAccessPolicy.CanUse',
):
    if marker not in sessions:
        die(f'session security marker missing: {marker}')

access_policy = text('services/browser/api/Security/BrowserSessionAccessPolicy.cs')
for marker in (
    'if (!sessionOwnerIsAuthenticated)',
    'return true;',
    'callerIsAuthenticated',
    'string.Equals(sessionOwnerKey, callerOwnerKey, StringComparison.Ordinal)',
):
    if marker not in access_policy:
        die(f'session access policy lost required marker: {marker}')

access_check = text('tools/browser-session-access-check/Program.cs')
for marker in (
    '"anonymous changed network"',
    '"authenticated same user"',
    '"authenticated different user"',
    '"authenticated session without access token"',
):
    if marker not in access_check:
        die(f'session access regression coverage missing: {marker}')

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

telemetry = text('services/browser/api/Services/AiAccessTelemetryReporter.cs')
for required_marker in (
    'AiAccessTelemetryReporter',
    'api/internal/ai-access/events',
    'BoundedChannelFullMode.DropWrite',
    'Telemetry is intentionally fail-open',
):
    if required_marker not in telemetry:
        die(f'AI access telemetry reporter lost required marker: {required_marker}')
for forbidden_marker in ('Request.Cookies', 'Headers.Authorization', 'AccessToken', 'Password', 'RefreshToken', 'BrowserSessionToken'):
    if forbidden_marker in telemetry:
        die(f'AI access telemetry must not collect sensitive value: {forbidden_marker}')

support_telemetry = text('services/bots/support-bot/AiAccessTelemetry.cs')
for required_marker in (
    'MinNotificationIntervalSeconds',
    'DiscoveryOnlyMinEvents',
    'MaxEventsPerDigest',
    'IncludeFullIp',
    'IsKnownAiAgent',
):
    if required_marker not in support_telemetry:
        die(f'support-bot AI telemetry anti-spam marker missing: {required_marker}')

support_program = text('services/bots/support-bot/Program.cs')
if 'MapPost("/api/internal/ai-access/events"' not in support_program or 'InternalRequestAuthorized' not in support_program:
    die('support-bot AI telemetry ingest must remain internal-key protected')

identity_telemetry = text('services/identity/api/Services/Telemetry/AiAccessTelemetryClient.cs')
for forbidden_marker in ('Password', 'Email', 'Authorization', 'Cookie', 'AccessToken', 'RefreshToken'):
    if forbidden_marker in identity_telemetry:
        die(f'AI identity telemetry must not collect sensitive value: {forbidden_marker}')

for required_marker in (
    'MapGet("/ai-access"',
    'MapGet("/api/site/agent/playbook"',
    'MapGet("/api/site/agent/capture/{site}/{width:int}/{height:int}/{mode}/{**path}"',
    'MapGet("/ai-artifacts/{id}/{fileName}"',
    'PUBLIC_ARTIFACT_REQUIRES_ANONYMOUS',
):
    if required_marker not in program:
        die(f'crawler self-discovery route/security marker missing: {required_marker}')

if 'User-agent: MJ12bot' not in program or 'Disallow: /ai-artifacts/' not in program:
    die('temporary artifacts are no longer shielded from known traditional crawlers')
robots_wildcard = re.search(r'"User-agent: \*"(?P<body>.*?)(?:sitemap|Sitemap)', program, re.S)
if robots_wildcard and 'Disallow: /ai-artifacts/' in robots_wildcard.group('body'):
    die('robots.txt must not block temporary artifacts for every user-agent; dedicated AI clients need direct artifact access')

for marker in ('AutomationId', 'AutomationRole', 'AutomationAction', 'AutomationState', 'AutomationKind', 'SnapshotSelectOption'):
    if marker not in contracts:
        die(f'semantic snapshot AI contract marker missing: {marker}')
for request_name in ('ClickBrowserSessionRequest', 'FillBrowserSessionRequest', 'SelectBrowserSessionRequest', 'CheckBrowserSessionRequest'):
    request_body = next((body for name, body in request_records if name == request_name), '')
    if 'WaitMs' not in request_body:
        die(f'{request_name} no longer supports a deliberate post-action wait')

snapshot_builder = text('services/browser/api/Services/SnapshotBuilder.cs')
for marker in ('data-taskforge-automation-id', 'automationState', 'automationKind', 'HTMLSelectElement', 'interactiveElements'):
    if marker not in snapshot_builder:
        die(f'agent-friendly semantic snapshot marker missing: {marker}')

code_editor = text('apps/web/src/components/CodeEditor.jsx')
for marker in ('__TASKFORGE_BROWSER_AUTOMATION__', 'data-taskforge-automation-id'):
    if marker not in code_editor:
        die(f'agent-friendly code editor fallback marker missing: {marker}')
solve_editor = text('apps/web/src/features/assignment-solve/components/SolveDraftEditor.jsx')
if 'solution-code-editor' not in solve_editor:
    die('solve editor no longer exposes the stable solution-code-editor automation id')

for marker in ('CrawlerFamily', 'MJ12bot', 'Web crawler / MJ12bot'):
    if marker not in support_telemetry:
        die(f'crawler telemetry classification marker missing: {marker}')

agent_access = text('services/browser/api/Services/AgentAccessService.cs')
for required_marker in ('TaskForge AI / crawler access', '/api/site/agent/capture/', 'authoritative Chromium PNG', 'RequiresAuthentication'):
    if required_marker not in agent_access:
        die(f'agent access page lost required marker: {required_marker}')

artifact_store = text('services/browser/api/Services/PublicAgentArtifactStore.cs')
for required_marker in ('IncrementalHash.CreateHash(HashAlgorithmName.SHA256)', 'AgentArtifactTtlSeconds', 'MaxCachedArtifactBytes', 'tf:browser:agent-artifact:'):
    if required_marker not in artifact_store:
        die(f'public agent artifact store lost required marker: {required_marker}')

inspection = text('services/browser/api/Services/SiteInspectionService.cs')
for required_marker in ('CaptureAgentBundleAsync', 'var width = $"{capture.Width}px"', 'PDF is a compatibility wrapper around the authoritative Chromium PNG', 'return new AgentCaptureBundle(snapshot, png, null)'):
    if required_marker not in inspection:
        die(f'agent capture/PDF architecture marker missing: {required_marker}')
if 'pt"' in inspection or '}pt' in inspection:
    die('Playwright PDF dimensions must use documented px/in/cm/mm units; pt is not supported')

artifact_store = text('services/browser/api/Services/PublicAgentArtifactStore.cs')
for required_marker in ('bool HasPdf = true', 'RenderArtifact? pdf', 'if (pdf is not null)', 'manifest.HasPdf'):
    if required_marker not in artifact_store:
        die(f'public agent artifact optional-PDF resilience marker missing: {required_marker}')

frontend_index = text('apps/web/public/index.html')
for required_marker in ('href="/ai-access"', 'taskforge-ai-discovery', 'href="/llms.txt"'):
    if required_marker not in frontend_index:
        die(f'root HTML no longer advertises AI discovery without JavaScript: {required_marker}')

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

# Do not place the NuGet global-packages directory only in a BuildKit cache mount
# when a later layer publishes with --no-restore. A cached restore layer can retain
# project.assets.json while the ephemeral cache mount is empty on a fresh builder,
# which makes publish fail with NETSDK1064 even though restore previously succeeded.
if re.search(r'--mount=type=cache[^\n]*target=/root/\.nuget/packages', dockerfile):
    die('Browser Dockerfile must persist restored NuGet packages in the build layer; an external cache mount can detach project.assets.json from its package files')

routes = text('apps/gateway/snippets/api-routes.conf')
proxy = text('apps/gateway/snippets/proxy-common.conf')
cloudflare_real_ip = text('apps/gateway/snippets/cloudflare-real-ip.conf')
for marker in ('^/api/(site|browser)', 'limit_req zone=tf_browser_public', 'limit_conn tf_browser_connections', 'limit_conn tf_browser_capture_connections', 'limit_conn_status 429', 'location = /ai-access', 'location = /sitemap.xml', 'location ^~ /ai-artifacts/'):
    if marker not in routes:
        die(f'gateway Browser API protection missing: {marker}')
if 'X-TaskForge-Client-IP $remote_addr' not in proxy or 'CF-Connecting-IP ""' not in proxy:
    die('gateway does not overwrite untrusted client-IP headers before proxying upstream')
if 'real_ip_header CF-Connecting-IP;' not in cloudflare_real_ip or 'real_ip_recursive on;' not in cloudflare_real_ip:
    die('gateway does not restore Cloudflare visitor IPs through nginx real_ip')
if cloudflare_real_ip.count('set_real_ip_from ') != 22:
    die('Cloudflare trusted proxy list must contain the complete 15 IPv4 + 7 IPv6 published ranges')
for marker in ('173.245.48.0/20', '104.16.0.0/13', '172.64.0.0/13', '2400:cb00::/32', '2a06:98c0::/29', '2c0f:f248::/32'):
    if marker not in cloudflare_real_ip:
        die(f'Cloudflare trusted proxy range missing: {marker}')
for template in ('dev.conf', 'http.conf', 'https.conf'):
    value = text(f'apps/gateway/templates/{template}')
    include_marker = 'include /etc/nginx/snippets/cloudflare-real-ip.conf;'
    limit_marker = 'limit_req_zone $binary_remote_addr zone=tf_browser_public'
    if include_marker not in value:
        die(f'{template} does not load trusted Cloudflare real-IP configuration')
    if limit_marker not in value:
        die(f'{template} has no Browser API request zone')
    if value.index(include_marker) > value.index(limit_marker):
        die(f'{template} restores real client IP after the Browser API rate-limit zone is defined')
    if 'limit_conn_zone $binary_remote_addr zone=tf_browser_connections' not in value:
        die(f'{template} has no Browser API connection zone')
    if 'limit_conn_zone $binary_remote_addr zone=tf_browser_capture_connections' not in value:
        die(f'{template} has no dedicated Browser capture connection zone')

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
