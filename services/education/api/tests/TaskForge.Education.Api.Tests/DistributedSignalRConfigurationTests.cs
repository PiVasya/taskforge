using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskForge.Cluster;
using Xunit;

namespace TaskForge.Education.Api.Tests;

public sealed class DistributedSignalRConfigurationTests
{
    private static IConfiguration Configure(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value))
            .Build();

    [Fact]
    public void MigrationJobDoesNotRequireRuntimeSignalRNamespaceOrRedis()
    {
        var configuration = Configure(
            ("Cluster:MultiNode", "true"),
            ("Database:MigrationJobOnly", "true"));

        var services = new ServiceCollection();
        services.AddTaskForgeSignalR(configuration, "education-api");

        Assert.NotEmpty(services);
    }

    [Fact]
    public void RuntimeClusterRejectsMissingNamespace()
    {
        var configuration = Configure(("Cluster:MultiNode", "true"));

        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddTaskForgeSignalR(configuration, "education-api"));
        Assert.Contains("Cluster__Namespace", error.Message);
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("../other-cluster")]
    [InlineData("a:b")]
    public void RuntimeClusterRejectsInvalidNamespaces(string name)
    {
        var configuration = Configure(
            ("Cluster:MultiNode", "true"),
            ("Cluster:Namespace", name),
            ("ConnectionStrings:Redis", "redis:6379"));

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddTaskForgeSignalR(configuration, "education-api"));
    }

    [Fact]
    public void RuntimeClusterAcceptsNamespacedRedisBackplaneConfiguration()
    {
        var configuration = Configure(
            ("Cluster:MultiNode", "true"),
            ("Cluster:Namespace", "taskforge-k3s-lab"),
            ("ConnectionStrings:Redis", "redis:6379"));

        var services = new ServiceCollection();
        services.AddTaskForgeSignalR(configuration, "education-api");

        Assert.NotEmpty(services);
    }

    [Fact]
    public void SingleNodeRuntimeDoesNotRequireRedisBackplane()
    {
        var services = new ServiceCollection();
        services.AddTaskForgeSignalR(Configure(), "education-api");
        Assert.NotEmpty(services);
    }
}
