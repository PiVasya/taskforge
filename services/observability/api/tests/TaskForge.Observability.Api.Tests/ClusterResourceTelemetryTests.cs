using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskForge.Observability.Api.Services.Cluster;
using Xunit;

namespace TaskForge.Observability.Api.Tests;

public sealed class ClusterResourceTelemetryTests
{
    [Fact]
    public void Missing_measurements_are_not_zero_and_real_zero_is_preserved()
    {
        var history = JsonNode.Parse("""[{"at":"2026-10-17T21:00:00Z","cpu_percent":0,"memory_percent":50},{"at":"2026-10-17T21:00:05Z","cpu_percent":null,"temperature_c":900}]""")!.AsArray();
        var actual = ClusterTelemetryNormalizer.HostHistory(history);
        Assert.Equal(0d, actual[0]!["cpu_percent"]!.GetValue<double>());
        Assert.Null(actual[0]!["temperature_c"]);
        Assert.Null(actual[1]!["cpu_percent"]);
        Assert.Null(actual[1]!["temperature_c"]);
    }

    [Fact]
    public void Host_history_is_bounded_and_bad_timestamps_are_ignored()
    {
        var history = new JsonArray();
        var start = DateTimeOffset.Parse("2026-10-17T21:00:00Z");
        for (var i = 0; i < 1000; i++) history.Add(new JsonObject { ["at"] = start.AddSeconds(i).ToString("O"), ["cpu_percent"] = i % 100 });
        history.Add(new JsonObject { ["at"] = "bad", ["cpu_percent"] = 12 });
        var actual = ClusterTelemetryNormalizer.HostHistory(history);
        Assert.Equal(719, actual.Count);
        Assert.DoesNotContain(actual.OfType<JsonObject>(), row => row["at"]!.GetValue<string>() == "bad");
    }

    [Fact]
    public void Normalizer_keeps_measured_history_but_does_not_mutate_input()
    {
        var raw = JsonNode.Parse("""{"node":{"id":"A"},"host":{"sampled_at":"2026-10-17T21:00:00Z","sample_interval_seconds":5,"cpu_percent":12.5,"history":[{"at":"2026-10-17T21:00:00Z","cpu_percent":12.5,"memory_percent":43,"temperature_c":62.5,"secret":"do-not-copy"}],"temperature_sensors":[{"sensor":"coretemp:Package","c":62.5}]}}""")!.AsObject();
        var before = raw.ToJsonString();
        var actual = ClusterTelemetryNormalizer.Normalize(raw);
        Assert.Single(actual["host"]!["history"]!.AsArray());
        Assert.Equal(62.5, actual["host"]!["history"]![0]!["temperature_c"]!.GetValue<double>());
        Assert.DoesNotContain("do-not-copy", actual.ToJsonString());
        Assert.Equal(before, raw.ToJsonString());
    }

    [Theory]
    [InlineData("ha.container_start_failed", "error", false)]
    [InlineData("ha.container_recreate_failed", "error", false)]
    [InlineData("ha.container_reconcile_failed", "error", false)]
    [InlineData("update.incident", "error", true)]
    [InlineData("update.recovered", "success", true)]
    [InlineData("cluster.node_down", "error", true)]
    public void Telegram_uses_incidents_not_legacy_compose_repeats(string kind, string severity, bool expected)
    {
        var method = typeof(ClusterTelemetryService).GetMethod("ShouldNotify", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)method.Invoke(null, new object[] { kind, severity })!);
    }

    [Fact]
    public void Public_updater_contract_handles_node_agent_and_legacy_nodes()
    {
        var method = typeof(ClusterTelemetryService).GetMethod("BuildPublicUpdate", BindingFlags.Static | BindingFlags.NonPublic)!;
        var update = JsonNode.Parse("""{"mode":"node-agent","running":true,"status":"monitoring","failed_count":1,"pending_count":1,"services":[{"service":"quiz-api","status":"rolled-back","current_image":"sha256:good","target_image":"sha256:bad","error":"healthcheck-failed","retry_in_seconds":30,"private_env":"never-export"}]}""")!.AsObject();
        var actual = JsonSerializer.SerializeToNode(method.Invoke(null, new object[] { update }))!;
        Assert.True(actual["running"]!.GetValue<bool>());
        Assert.True(actual["hasError"]!.GetValue<bool>());
        Assert.Equal("sha256:good", actual["services"]![0]!["currentImage"]!.GetValue<string>());
        Assert.DoesNotContain("never-export", actual.ToJsonString());
        var old = JsonNode.Parse("""{"mode":"watchtower","watchtower_running":true}""")!.AsObject();
        var compatible = JsonSerializer.SerializeToNode(method.Invoke(null, new object[] { old }))!;
        Assert.True(compatible["running"]!.GetValue<bool>());
    }
}
