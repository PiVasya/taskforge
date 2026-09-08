using System.Globalization;
using System.Text.Json.Nodes;

namespace TaskForge.Observability.Api.Services.Cluster;

// Adapt the read-only telemetry contract. Never infer an image digest from a tag
// or treat a missing measurement as zero.
internal static class ClusterTelemetryNormalizer
{
    internal static string? Text(JsonNode? value)
        => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    internal static double? Number(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var n) && double.IsFinite(n)) return n;
        if (v.TryGetValue<long>(out var whole)) return whole;
        if (v.TryGetValue<int>(out var small)) return small;
        return double.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
            && double.IsFinite(n) ? n : null;
    }
    private static JsonObject Pick(JsonObject? source, params string[] names)
    {
        var result = new JsonObject();
        if (source is not null)
            foreach (var name in names) if (source[name] is { } value) result[name] = value.DeepClone();
        return result;
    }
    internal static JsonObject Network(JsonObject? node)
    {
        var wg = Text(node?["wireguard_ip"]);
        var url = Text(node?["agent_url"]);
        if (wg is null && Uri.TryCreate(url, UriKind.Absolute, out var uri)) wg = uri.Host;
        return new JsonObject
        {
            ["publicHost"] = Text(node?["public_host"]),
            ["wireguardIp"] = wg,
            ["agentUrl"] = url,
            ["httpPort"] = node?["http_port"]?.DeepClone(),
            ["httpsPort"] = node?["https_port"]?.DeepClone(),
            ["agentPort"] = node?["health_port"]?.DeepClone(),
        };
    }
    internal static JsonObject Normalize(JsonObject source)
    {
        var p = (JsonObject)source.DeepClone();
        var node = p["node"] as JsonObject ?? new JsonObject();
        if (p["node"] is null) p["node"] = node;
        var topology = new JsonArray();
        foreach (var item in (p["topology"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var wg = Text(item["wireguard"]?["ip"]) ?? Text(item["wireguard_ip"]);
            var port = (int?)(Number(item["health_port"])) ?? 9187;
            var host = wg?.Contains(':') == true ? $"[{wg}]" : wg;
            var entry = new JsonObject
            {
                ["id"] = Text(item["id"]),
                ["agent_url"] = Text(item["agent_url"]) ?? (host is null ? null : $"http://{host}:{port}"),
                ["app_profile"] = Text(item["app_profile"]) ?? Text(item["app"]?["profile"]),
                ["can_be_primary"] = (item["can_be_primary"] ?? item["app"]?["can_be_primary"])?.DeepClone(),
                ["dcs_voter"] = item["dcs_voter"]?.DeepClone(),
                ["public_host"] = Text(item["public_host"]),
                ["wireguard_ip"] = wg,
                ["health_port"] = port,
                ["http_port"] = item["http_port"]?.DeepClone(),
                ["https_port"] = item["https_port"]?.DeepClone(),
                // Internal-only control metadata. Network() deliberately does not
                // expose the Patroni REST port to the browser, but the admin
                // control plane needs the real configured port instead of guessing.
                ["patroni_rest_port"] = item["postgres"]?["patroni_rest_port"]?.DeepClone()
                    ?? item["patroni_rest_port"]?.DeepClone(),
            };
            topology.Add(entry);
            if (Text(entry["id"]) == Text(node["id"]))
                foreach (var field in new[] { "public_host", "wireguard_ip", "agent_url", "health_port", "http_port", "https_port" })
                    node[field] = entry[field]?.DeepClone();
        }
        p["topology"] = topology;

        var originalHost = p["host"] as JsonObject;
        var hostInfo = Pick(originalHost, "hostname", "kernel", "cpu_count", "cpu_percent", "load", "memory", "swap", "disk", "uptime_seconds", "temperature_c");
        if (hostInfo["disk"] is null && Number(originalHost?["disk_root_total_bytes"]) is { } total && total > 0
            && Number(originalHost?["disk_root_free_bytes"]) is { } available && available >= 0 && available <= total)
            hostInfo["disk"] = new JsonObject { ["total"] = total, ["available"] = available, ["used"] = total - available, ["includes_reserved"] = true };
        p["host"] = hostInfo;

        var docker = p["docker"] as JsonObject ?? new JsonObject();
        if (p["docker"] is null) p["docker"] = docker;
        var containers = new JsonArray();
        foreach (var c in (docker["containers"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var clean = Pick(c, "service", "name", "health", "started_at", "cpu", "memory", "cpu_percent", "memory_bytes", "memory_limit_bytes", "image_fingerprint");
            clean["state"] = Text(c["state"]) ?? Text(c["status"]) ?? "unknown";
            clean["restart_count"] = (int?)(Number(c["restart_count"]) ?? Number(c["restart"]));
            clean["oom"] = c["oom"]?.DeepClone();
            containers.Add(clean);
        }
        var services = new JsonArray();
        foreach (var item in docker["services"] as JsonArray ?? new())
        {
            var obj = item as JsonObject;
            var name = Text(obj?["service"]) ?? Text(item);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var c = containers.OfType<JsonObject>().FirstOrDefault(x => Text(x["service"]) == name);
            var svc = Pick(obj, "service", "assigned", "desired_state", "container_state", "health", "image_fingerprint", "image_available");
            svc["service"] = name;
            if (svc["assigned"] is null) svc["assigned"] = true;
            svc["container_state"] ??= c?["state"]?.DeepClone();
            svc["health"] ??= c?["health"]?.DeepClone();
            svc["image_fingerprint"] ??= c?["image_fingerprint"]?.DeepClone();
            var state = Text(svc["container_state"]);
            if (svc["image_available"] is null && state is not null && state != "unknown")
                svc["image_available"] = state != "missing";
            services.Add(svc);
        }
        docker["containers"] = containers;
        docker["services"] = services;
        p["wireguard"] = new JsonObject
        {
            ["interface"] = Text(p["wireguard"]?["interface"]),
            ["peers"] = new JsonArray((p["wireguard"]?["peers"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(x => (JsonNode)Pick(x, "allowed_ips", "endpoint", "latest_handshake_epoch", "rx_bytes", "tx_bytes")).ToArray()),
        };
        p["edge"] = PublicEdge(p["edge"] as JsonObject);
        return p;
    }
    internal static JsonObject PublicEdge(JsonObject? edge)
    {
        var result = Pick(edge, "provider", "configured", "last_success_epoch");
        var state = Pick(edge?["state"] as JsonObject, "success", "status", "phase", "route_ready", "tls_ready", "dns_synced", "last_error", "target_node", "target_ip", "route_confirmations", "route_required", "elapsed_seconds", "wait_limit_seconds", "certificate_action", "http_only", "warning", "verified_at", "retry_in_seconds");
        state["route_checks"] = new JsonArray((edge?["state"]?["route_checks"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(x => (JsonNode)Pick(x, "host", "scheme", "http", "ok", "reason", "cloudflare")).ToArray());
        result["state"] = state;
        return result;
    }
}
