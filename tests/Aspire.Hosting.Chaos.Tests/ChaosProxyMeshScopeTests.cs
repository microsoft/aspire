// <copyright file="ChaosProxyMeshScopeTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests <see cref="ChaosMeshScope"/> — the explicit include-allowlist that scopes the mesh to a
/// subset of edges to cut cold-start proxy-container tax. Covers the env-value parser, that an unset
/// scope is byte-identical to a full mesh, that a scoped run meshes ONLY the requested edges (leaving
/// the rest direct with authored <c>WaitFor</c> intact), and the fail-closed completeness gate.
/// </summary>
[SuppressMessage("AspireExperimental", "ASPIRECHAOS001", Justification = "test")]
public class ChaosProxyMeshScopeTests
{
    private static IDistributedApplicationBuilder CreateBuilder()
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            AssemblyName = typeof(ChaosProxyMeshScopeTests).Assembly.GetName().Name,
        });
    }

    private static IResourceBuilder<ContainerResource> AddService(IDistributedApplicationBuilder builder, string name)
        => builder.AddContainer(name, "fake-image").WithHttpEndpoint(targetPort: 8080, name: "http");

    // ---- FromEnvironmentValue parser -----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvironmentValue_NullOrWhitespace_ReturnsNull(string? raw)
    {
        Assert.Null(ChaosMeshScope.FromEnvironmentValue(raw));
    }

    [Fact]
    public void FromEnvironmentValue_SingleEdge_Parses()
    {
        var scope = ChaosMeshScope.FromEnvironmentValue("armgatewayservice-api->cosmos");

        Assert.NotNull(scope);
        Assert.Equal(new[] { ("armgatewayservice-api", "cosmos") }, scope!.Edges);
    }

    [Fact]
    public void FromEnvironmentValue_MultipleEdges_ParsesInOrder_TrimsAndToleratesTrailingComma()
    {
        var scope = ChaosMeshScope.FromEnvironmentValue(" a->b , c->d ,");

        Assert.NotNull(scope);
        Assert.Equal(new[] { ("a", "b"), ("c", "d") }, scope!.Edges);
    }

    [Fact]
    public void FromEnvironmentValue_DuplicateEdges_Deduplicated()
    {
        var scope = ChaosMeshScope.FromEnvironmentValue("a->b, a->b, A->B");

        Assert.NotNull(scope);
        Assert.Single(scope!.Edges);
    }

    [Theory]
    [InlineData("a-b")]           // no separator
    [InlineData("a->b->c")]       // too many separators
    [InlineData("->b")]           // empty client
    [InlineData("a->")]           // empty target
    [InlineData(",")]             // only separators, no valid edge
    public void FromEnvironmentValue_Malformed_Throws(string raw)
    {
        Assert.Throws<FormatException>(() => ChaosMeshScope.FromEnvironmentValue(raw));
    }

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        var scope = ChaosMeshScope.FromEnvironmentValue("Client->Target");

        Assert.NotNull(scope);
        Assert.True(scope!.Contains("client", "target"));
        Assert.True(scope.Contains("CLIENT", "TARGET"));
        Assert.False(scope.Contains("client", "other"));
    }

    // ---- unset scope == full mesh (byte-identical back-compat) ---------------------------

    [Fact]
    public void NullScope_MeshesAllEligibleEdges()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "target");
        var other = AddService(builder, "other");
        AddService(builder, "client")
            .WithReference(target.GetEndpoint("http"))
            .WithReference(other.GetEndpoint("http"));

        builder.AddChaosProxyMesh(scope: null);

        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-target");
        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-other");
    }

    // ---- scoped mesh: only requested edges ----------------------------------------------

    [Fact]
    public void ScopedMesh_MeshesOnlyRequestedEdge_LeavesOthersDirect()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "target");
        var other = AddService(builder, "other");
        AddService(builder, "client")
            .WithReference(target.GetEndpoint("http"))
            .WithReference(other.GetEndpoint("http"));

        var scope = ChaosMeshScope.FromEnvironmentValue("client->target");
        var mesh = builder.AddChaosProxyMesh(scope: scope);

        // Requested edge meshed; the other edge left direct (no proxy resource).
        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-target");
        Assert.DoesNotContain(builder.Resources, r => r.Name == "mesh-client-to-other");

        // The skipped edge is reported (never a silent no-op) with the scope reason.
        Assert.Contains(
            mesh.Summary,
            r => !r.Meshed && r.ClientName == "client" && r.TargetName == "other" &&
                 r.SkipReason == "out of mesh scope");
    }

    [Fact]
    public void ScopedMesh_PreservesAuthoredWaitFor_OnOutOfScopeEdge()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "target");
        var other = AddService(builder, "other");
        var client = AddService(builder, "client")
            .WithReference(target.GetEndpoint("http"))
            .WithReference(other.GetEndpoint("http"))
            .WaitFor(other);

        var scope = ChaosMeshScope.FromEnvironmentValue("client->target");
        builder.AddChaosProxyMesh(scope: scope);

        // The out-of-scope edge's authored WaitFor(other) must still be present — scoping must not
        // remove real dependency ordering, only skip proxy insertion.
        Assert.Contains(
            client.Resource.Annotations.OfType<WaitAnnotation>(),
            w => w.Resource.Name == "other");
    }

    // ---- fail-closed completeness gate ---------------------------------------------------

    [Fact]
    public void Seal_Passes_WhenAllRequestedEdgesMeshed()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "target");
        AddService(builder, "client").WithReference(target.GetEndpoint("http"));

        var scope = ChaosMeshScope.FromEnvironmentValue("client->target");
        var mesh = builder.AddChaosProxyMesh(scope: scope);

        // Idempotent, no throw.
        mesh.Seal();
        mesh.Seal();

        Assert.Contains(builder.Resources, r => r.Name == "mesh-client-to-target");
    }

    [Fact]
    public void Seal_Throws_WhenRequestedEdgeDoesNotExist()
    {
        var builder = CreateBuilder();
        var target = AddService(builder, "target");
        AddService(builder, "client").WithReference(target.GetEndpoint("http"));

        var scope = ChaosMeshScope.FromEnvironmentValue("client->ghost");
        var mesh = builder.AddChaosProxyMesh(scope: scope);

        var ex = Assert.Throws<InvalidOperationException>(() => mesh.Seal());
        Assert.Contains("client->ghost", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ChaosMeshScope.EnvironmentVariableName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_Throws_WhenRequestedEdgeIsStructurallyUnmeshable()
    {
        var builder = CreateBuilder();

        // target is referenced (so the edge IS considered) but exposes no "http" endpoint, so the
        // provider reports it skipped for a structural reason — a considered-but-unmeshable edge.
        var target = builder.AddContainer("target", "fake-image")
            .WithEndpoint(targetPort: 9000, name: "grpc", scheme: "http");
        AddService(builder, "client").WithReference(target.GetEndpoint("grpc"));

        var scope = ChaosMeshScope.FromEnvironmentValue("client->target");
        var mesh = builder.AddChaosProxyMesh(scope: scope);

        var ex = Assert.Throws<InvalidOperationException>(() => mesh.Seal());
        Assert.Contains("client->target", ex.Message, StringComparison.Ordinal);
    }
}
