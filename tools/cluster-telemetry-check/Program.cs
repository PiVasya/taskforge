using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using TaskForge.Observability.Api.Services.Cluster;

var assertions = 0;
void Expect(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
    Console.WriteLine("PASS: " + message);
}
JsonObject Fixture(string id, string role, bool certificate = true)
{
    var payload = JsonNode.Parse("""
    {
      "cluster":"test-cluster", "schema_version":1,
      "node":{"id":"A","dcs_voter":true,"can_be_primary":true,"app_profile":"full","bundle_revision":"57"},
      "ha":{"role":"primary","leader":"A","traffic_ready":true,"hot_start_ready":true,"hot_start_blockers":[],"traffic_blockers":[],"tls_ready":true,"tls_assets_ready":true,"tls_live_ready":true,"applications_active":true,"degraded_services":[],"reconciled":true},
      "topology":[
        {"id":"A","public_host":"198.51.100.10","dcs_voter":true,"app":{"profile":"full","can_be_primary":true},"wireguard":{"ip":"10.80.0.1","public_key":"DO_NOT_EXPORT"},"health_port":9187},
        {"id":"B","public_host":"198.51.100.20","dcs_voter":true,"app":{"profile":"full","can_be_primary":true},"wireguard":{"ip":"10.80.0.2","public_key":"DO_NOT_EXPORT"},"health_port":9187},
        {"id":"C","public_host":"198.51.100.30","dcs_voter":true,"app":{"profile":"lite","can_be_primary":true},"wireguard":{"ip":"10.80.0.3","public_key":"DO_NOT_EXPORT"},"health_port":9187}
      ],
      "host":{"status":"ok","uptime_seconds":250000,"disk_root_total_bytes":1000,"disk_root_free_bytes":300},
      "firewall":{"status":"active"},"minio":{"ready":true},"postgres":{"healthy":true},
      "wireguard":{"interface":"wg-taskforge","peers":[{"public_key":"DO_NOT_EXPORT","endpoint":"198.51.100.20:51820","allowed_ips":"10.80.0.2/32","latest_handshake_epoch":1234}]},
      "docker":{"services":["gateway","identity-api"],"containers":[{"service":"gateway","status":"running","health":"healthy","restart":2,"oom":false},{"service":"identity-api","status":"running","health":"healthy","restart":0,"oom":false}],"assigned_app_count":2,"prepared_app_count":2,"images_ready":2,"images_missing":0},
      "update":{"mode":"watchtower","watchtower_running":true,"status":"running","error":"","watchtower_error":"","poll_interval_seconds":300},
      "edge":{"provider":"cloudflare-dns","configured":true,"token":"DO_NOT_EXPORT","state":{"success":true,"phase":"ready","dns_synced":true,"route_ready":true,"tls_ready":true,"target_node":"A","target_ip":"198.51.100.10","route_confirmations":1,"route_required":1}},
      "events":[{"at":"2026-01-01T00:00:00Z","kind":"edge.public_ready","title":"Verified","detail":"route proof","severity":"success"}]
    }
    """)!.AsObject();
    payload["generated_at"] = DateTimeOffset.UtcNow.ToString("O");
    payload["node"]!["id"] = id;
    payload["node"]!["app_profile"] = id == "C" ? "lite" : "full";
    payload["ha"]!["role"] = role;
    payload["ha"]!["tls_ready"] = certificate;
    payload["ha"]!["tls_assets_ready"] = certificate;
    payload["ha"]!["tls_live_ready"] = role == "primary" && certificate;
    payload["ha"]!["traffic_ready"] = role == "primary";
    return payload;
}
var raw = Fixture("A", "primary");
var normalized = ClusterTelemetryNormalizer.Normalize(raw);
Expect(normalized["host"]!["disk"]!["used"]!.GetValue<double>() == 700, "r57 flat disk maps to measured disk use");
Expect(normalized["host"]!["memory"] is null, "missing RAM is not fabricated");
Expect(normalized["docker"]!["containers"]![0]!["state"]!.GetValue<string>() == "running", "r57 status maps to container state");
Expect(normalized["docker"]!["containers"]![0]!["restart_count"]!.GetValue<int>() == 2, "r57 restart count is retained");
Expect(normalized["docker"]!["services"]![0]!["image_available"]!.GetValue<bool>(), "prepared container proves image availability only");
Expect(normalized["docker"]!["services"]![0]!["image_fingerprint"] is null, "image fingerprints are never invented");
Expect(ClusterTelemetryNormalizer.Network(normalized["node"]!.AsObject())["publicHost"]!.GetValue<string>() == "198.51.100.10", "public IP comes from topology");
Expect(!normalized.ToJsonString().Contains("DO_NOT_EXPORT"), "network and edge projections exclude keys and tokens");
Expect(raw.ToJsonString().Contains("DO_NOT_EXPORT"), "normalizer does not mutate its input");

var path = Path.GetTempFileName();
try
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {
        ["ClusterTelemetry:LocalTelemetryPath"] = path,
        ["ClusterTelemetry:NotificationsEnabled"] = "false"
    }).Build();
    var service = new ClusterTelemetryService(new NoHttp(), config, NullLogger<ClusterTelemetryService>.Instance);
    var poll = typeof(ClusterTelemetryService).GetMethod("PollLocalTelemetryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    async Task Add(JsonObject fixture)
    {
        File.WriteAllText(path, fixture.ToJsonString());
        await (Task)poll.Invoke(service, [CancellationToken.None])!;
    }
    await Add(Fixture("A", "primary"));
    await Add(Fixture("B", "standby"));
    await Add(Fixture("C", "standby", false));
    JsonObject Snapshot() => JsonSerializer.SerializeToNode(service.BuildPublicSnapshot())!.AsObject();
    var snapshot = Snapshot();
    Expect(snapshot["schemaVersion"]!.GetValue<int>() == 2, "dashboard contract is versioned");
    Expect(snapshot["status"]!.GetValue<string>() == "healthy", "certificate-less hot standby does not degrade a healthy cluster");
    Expect(snapshot["summary"]!["images"]!.GetValue<string>() == "unverified", "no digests is unverified, not synchronized");
    Expect(snapshot["summary"]!["imageAvailable"]!.GetValue<int>() == 6, "image availability aggregation is populated");
    Expect(snapshot["summary"]!["imageMissing"]!.GetValue<int>() == 0, "unknown fingerprint is not missing image");
    Expect(snapshot["summary"]!["quorumTotal"]!.GetValue<int>() == 3, "r57 topology provides all expected voters");
    var c = snapshot["nodes"]!.AsArray().OfType<JsonObject>().Single(n => n["id"]!.GetValue<string>() == "C");
    Expect(c["appProfile"]!.GetValue<string>() == "lite", "C remains lite");
    Expect(c["network"]!["wireguardIp"]!.GetValue<string>() == "10.80.0.3", "WireGuard address is not discarded");
    Expect(snapshot["events"]!.AsArray().Count == 3, "id-less agent events remain visible");
    Expect(snapshot["events"]![0]!["message"]!.GetValue<string>() == "route proof", "event detail maps to message");
    Expect(!snapshot.ToJsonString().Contains("DO_NOT_EXPORT"), "public snapshot contains no topology keys or API tokens");
    var stableIds = snapshot["events"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToArray();
    Expect(stableIds.SequenceEqual(Snapshot()["events"]!.AsArray().Select(e => e!["id"]!.GetValue<string>())), "synthesized event IDs are stable between snapshots");
    await Add(Fixture("B", "primary"));
    snapshot = Snapshot();
    Expect(snapshot["summary"]!["primaryConflict"]!.GetValue<bool>(), "conflicting primaries are exposed, never arbitrarily selected");
    Expect(snapshot["summary"]!["activeNode"] is null, "ambiguous primary has no fabricated active node");
    var stale = Fixture("B", "primary");
    stale["generated_at"] = DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O");
    await Add(stale);
    Expect(!Snapshot()["summary"]!["primaryConflict"]!.GetValue<bool>(), "stale primary does not decide current leadership");
    await Add(Fixture("B", "standby"));
    var oldA = Fixture("A", "primary");
    oldA["docker"]!["services"] = JsonNode.Parse("""[{"service":"gateway","assigned":true,"image_fingerprint":"sha256:one","container_state":"running"},{"service":"identity-api","assigned":true,"image_fingerprint":"sha256:two","container_state":"running"}]""");
    await Add(oldA);
    snapshot = Snapshot();
    var a = snapshot["nodes"]!.AsArray().OfType<JsonObject>().Single(n => n["id"]!.GetValue<string>() == "A");
    Expect(a["services"]![0]!["imageStatus"]!.GetValue<string>() == "same", "object-shaped telemetry with real fingerprint still compares");
}
finally { File.Delete(path); }
Console.WriteLine($"Cluster telemetry assertions: {assertions} PASS");

sealed class NoHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("Tests must not call the network");
}
