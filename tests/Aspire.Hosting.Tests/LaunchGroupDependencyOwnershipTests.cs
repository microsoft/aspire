// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests the experimental launch-group APIs.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class LaunchGroupDependencyOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedDatabaseRequiresItsServerRegardlessOfChildMembership(bool childSelected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server").WithLaunchGroups("backend");
        var database = server.AddDatabase("database");
        if (childSelected)
        {
            database.WithLaunchGroups("frontend");
        }
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'server' (the lifetime owner of 'database'), " +
            "which is not included in launch group 'frontend'. Dependency path: frontend -> database -> server. " +
            "Edges: frontend --reference--> database; database --lifetime owner--> server. Add 'server' to launch group 'frontend'. " +
            "If the reference from 'frontend' to 'database' is optional, mark it with WithOptionalLaunchGroupReference.",
            exception.Message);
    }

    [Fact]
    public async Task ReferencedDatabaseDoesNotNeedMembershipWhenItsServerIsSelected()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server").WithLaunchGroups("frontend");
        var database = server.AddDatabase("database");
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(server.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(database.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Empty(database.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedDatabaseRequiresItsExplicitStartServer(bool excludedFromSelection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server").WithLaunchGroups("frontend").WithExplicitStart();
        if (excludedFromSelection)
        {
            server.WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        }
        var database = server.AddDatabase("database");
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'server' (the lifetime owner of 'database'), " +
            "which is marked for explicit start. Dependency path: frontend -> database -> server. " +
            "Edges: frontend --reference--> database; database --lifetime owner--> server. " +
            "Remove explicit start from 'server' and ensure it is included in launch group 'frontend'. " +
            "If the reference from 'frontend' to 'database' is optional, mark it with WithOptionalLaunchGroupReference.",
            exception.Message);
    }

    [Fact]
    public async Task ReferencedDatabaseCanUseServerExcludedFromSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server")
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        var database = server.AddDatabase("database");
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(server.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(database.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task SelectedPassiveChildIsNotAnAutomaticStartRoot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server").WithLaunchGroups("backend");
        var dependency = builder.AddExecutable("dependency", "unused", ".").WithLaunchGroups("backend");
        server.AddDatabase("database")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(dependency);
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(server.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ReferencedPassiveChildDependenciesAreValidated()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server").WithLaunchGroups("frontend");
        var dependency = builder.AddExecutable("dependency", "unused", ".").WithLaunchGroups("backend");
        var database = server.AddDatabase("database").WithReferenceRelationship(dependency);
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'dependency', which is not included in launch group 'frontend'. " +
            "Dependency path: frontend -> database -> dependency. Edges: frontend --reference--> database; database --reference--> dependency. " +
            "Add 'dependency' to launch group 'frontend'. " +
            "If the reference from 'database' to 'dependency' is optional, mark it with WithOptionalLaunchGroupReference.",
            exception.Message);
    }

    [Fact]
    public async Task ReferencedExecutableChildDoesNotRequireItsStructuralParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var parent = builder.AddExecutable("parent", "unused", ".");
        var child = builder.AddResource(new ChildExecutableResource("child", parent.Resource))
            .WithLaunchGroups("frontend");
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(child);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(parent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ReferencedExecutableChildInheritsItsParentsMembership()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("frontend");
        var child = builder.AddResource(new ChildExecutableResource("child", parent.Resource));
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(child);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(parent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Empty(child.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildInheritsGroupsFromParentOutsideModel(bool configureGroup)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        if (configureGroup)
        {
            builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        }
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("catalog");
        var child = builder.AddResource(new ChildExecutableResource("child", parent.Resource));
        builder.Resources.Remove(parent.Resource);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Empty(child.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedChildrenRespectLifetimeWhenParentIsOutsideModel(bool passive)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var owner = builder.AddExecutable("owner", "unused", ".");
        IResource childResource = passive
            ? new ChildWithoutLifetime("child", owner.Resource)
            : new ChildExecutableResource("child", owner.Resource);
        var child = builder.AddResource(childResource).WithLaunchGroups("frontend");
        builder.Resources.Remove(owner.Resource);
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(child);
        await using var app = builder.Build();

        if (passive)
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
                () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

            Assert.Contains("local resource 'owner' (the lifetime owner of 'child')", exception.Message);
            Assert.Contains("Dependency path: frontend -> child -> owner.", exception.Message);
            Assert.Contains("frontend --reference--> child; child --lifetime owner--> owner", exception.Message);
        }
        else
        {
            await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

            Assert.False(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
            Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other")]
    public async Task UndefinedInheritedGroupFromParentOutsideModelFails(string? selection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        builder.Configuration["Aspire:LaunchGroups:0"] = "other";
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("catalog");
        var child = builder.AddResource(new ChildExecutableResource("child", parent.Resource));
        builder.Resources.Remove(parent.Resource);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Resource 'child' belongs to undefined launch group 'catalog'.", exception.Message);
        Assert.False(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("CATALOG", false, false, false, true, true, false)]
    [InlineData("basket", true, true, true, false, false, true)]
    [InlineData("shared", false, false, false, false, false, true)]
    public async Task StructuralChildrenInheritNearestDeclaredGroups(
        string selection,
        bool parentExplicit,
        bool childExplicit,
        bool grandchildExplicit,
        bool overrideExplicit,
        bool overrideChildExplicit,
        bool explicitLeafExplicit)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("catalog", "shared");
        var child = builder.AddResource(new ChildExecutableResource("child", parent.Resource));
        var grandchild = builder.AddResource(new ChildExecutableResource("grandchild", child.Resource));
        var overridden = builder.AddResource(new ChildExecutableResource("overridden", parent.Resource))
            .WithLaunchGroups("basket", "shared").WithLaunchGroups("BASKET");
        var overrideChild = builder.AddResource(new ChildExecutableResource("override-child", overridden.Resource));
        var explicitLeaf = builder.AddResource(new ChildExecutableResource("explicit-leaf", overridden.Resource))
            .WithLaunchGroups("catalog");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [parentExplicit, childExplicit, grandchildExplicit, overrideExplicit, overrideChildExplicit, explicitLeafExplicit],
            new[]
            {
                parent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>(),
                child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>(),
                grandchild.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>(),
                overridden.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>(),
                overrideChild.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>(),
                explicitLeaf.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()
            });
        Assert.Equal(["catalog", "shared"], parent.Resource.Annotations.OfType<LaunchGroupAnnotation>().Select(a => a.LaunchGroupName));
        Assert.Equal(["basket", "shared"], overridden.Resource.Annotations.OfType<LaunchGroupAnnotation>().Select(a => a.LaunchGroupName));
        Assert.Empty(child.Resource.Annotations.OfType<LaunchGroupAnnotation>());
        Assert.Empty(grandchild.Resource.Annotations.OfType<LaunchGroupAnnotation>());
        Assert.Empty(overrideChild.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Theory]
    [InlineData("catalog", true)]
    [InlineData("backend", false)]
    public async Task NearestStructuralGroupsWinOverConflictingDisplayParent(string selection, bool childExplicit)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        var root = builder.AddExecutable("root", "unused", ".").WithLaunchGroups("catalog");
        var nearest = builder.AddResource(new ChildExecutableResource("nearest", root.Resource))
            .WithLaunchGroups("backend");
        var displayParent = builder.AddExecutable("display", "unused", ".").WithLaunchGroups("catalog");
        var child = builder.AddResource(new ChildExecutableResource("child", nearest.Resource))
            .WithParentRelationship(displayParent);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(childExplicit, nearest.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(childExplicit, child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(!childExplicit, root.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(!childExplicit, displayParent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Empty(child.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Fact]
    public async Task InheritedChildStillRequiresItsDeclaredDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("frontend");
        var dependency = builder.AddExecutable("dependency", "unused", ".").WithLaunchGroups("backend");
        var child = builder.AddResource(new ChildExecutableResource("child", parent.Resource))
            .WithReferenceRelationship(dependency);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'child' requires local resource 'dependency', which is not included in launch group 'frontend'. " +
            "Dependency path: child -> dependency. Edges: child --reference--> dependency. " +
            "Add 'dependency' to launch group 'frontend'. " +
            "If the reference from 'child' to 'dependency' is optional, mark it with WithOptionalLaunchGroupReference.",
            exception.Message);
        Assert.False(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task InheritedMembershipPreservesExplicitStartAndSelectionExclusions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var selectedParent = builder.AddExecutable("selected-parent", "unused", ".").WithLaunchGroups("frontend");
        var otherParent = builder.AddExecutable("other-parent", "unused", ".").WithLaunchGroups("backend");
        var dependency = builder.AddExecutable("dependency", "unused", ".").WithLaunchGroups("backend");
        var explicitChild = builder.AddResource(new ChildExecutableResource("explicit-child", selectedParent.Resource))
            .WithExplicitStart().WithReferenceRelationship(dependency);
        var excludedChild = builder.AddResource(new ChildExecutableResource("excluded-child", otherParent.Resource))
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(selectedParent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(otherParent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(explicitChild.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(excludedChild.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task CyclicStructuralParentsFailBeforeChangingSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("frontend");
        var first = builder.AddResource(new ChildExecutableResource("first", parent.Resource));
        var second = builder.AddResource(new ChildExecutableResource("second", first.Resource));
        first.Resource.Parent = second.Resource;
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal("A circular structural parent chain prevents determining launch-group membership for resource 'first'.", exception.Message);
        Assert.False(first.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(second.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitErrorsDoNotRecommendOptionalReferences(bool alsoReferenced)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var backend = builder.AddExecutable("backend", "unused", ".").WithLaunchGroups("backend");
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WaitFor(backend);
        if (alsoReferenced)
        {
            frontend.WithReferenceRelationship(backend);
        }
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'backend', which is not included in launch group 'frontend'. " +
            $"Dependency path: frontend -> backend. Edges: frontend --{(alsoReferenced ? "reference/wait" : "wait")}--> backend. " +
            "Add 'backend' to launch group 'frontend'. The wait from 'frontend' to 'backend' requires automatic startup.",
            exception.Message);
    }

    [Fact]
    public async Task DatabaseReferenceWithServerWaitDoesNotRecommendOptionalReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddPostgres("server").WithLaunchGroups("backend");
        var database = server.AddDatabase("database").WithLaunchGroups("frontend");
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database)
            .WaitFor(server);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'server' (the lifetime owner of 'database'), " +
            "which is not included in launch group 'frontend'. Dependency path: frontend -> database -> server. " +
            "Edges: frontend --reference--> database; database --lifetime owner--> server. Add 'server' to launch group 'frontend'. " +
            "The wait from 'frontend' to 'server' requires automatic startup.",
            exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWaitOnResourceWithoutLifetimeDoesNotRequireItsOwner(bool alsoReferenced)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddExecutable("server", "unused", ".").WithLaunchGroups("backend");
        var child = builder.AddResource(new ChildWithoutLifetime("child", server.Resource));
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WaitForCompletion(child);
        if (alsoReferenced)
        {
            frontend.WithReferenceRelationship(child);
        }
        await using var app = builder.Build();

        if (alsoReferenced)
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
                () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

            Assert.Equal(
                "Resource 'frontend' requires local resource 'server' (the lifetime owner of 'child'), " +
                "which is not included in launch group 'frontend'. Dependency path: frontend -> child -> server. " +
                "Edges: frontend --reference--> child; child --lifetime owner--> server. Add 'server' to launch group 'frontend'. " +
                "If the reference from 'frontend' to 'child' is optional, mark it with WithOptionalLaunchGroupReference.",
                exception.Message);
        }
        else
        {
            await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
            Assert.True(server.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitOnResourceWithoutLifetimeStillRequiresItsExplicitParentWait(bool waitForStart)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var server = builder.AddExecutable("server", "unused", ".").WithLaunchGroups("backend");
        var child = builder.AddResource(new ChildWithoutLifetime("child", server.Resource));
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        if (waitForStart)
        {
            frontend.WaitForStart(child);
        }
        else
        {
            frontend.WaitFor(child);
        }
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'server', which is not included in launch group 'frontend'. " +
            "Dependency path: frontend -> server. Edges: frontend --wait--> server. " +
            "Add 'server' to launch group 'frontend'. The wait from 'frontend' to 'server' requires automatic startup.",
            exception.Message);
    }

    [Fact]
    public async Task RedirectErrorsDoNotRecommendOptionalReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var target = builder.AddRedis("target").WithLaunchGroups("backend");
        var redirect = builder.AddRedis("redirect").WithLaunchGroups("frontend")
            .WithConnectionStringRedirection(target.Resource);
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend").WithReference(redirect);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Dependency path: frontend -> redirect -> target.", exception.Message);
        Assert.Contains("redirect --redirect--> target", exception.Message);
        Assert.EndsWith("The connection string redirection from 'redirect' to 'target' requires automatic startup.", exception.Message);
    }

    [Fact]
    public async Task OptionalReferenceDoesNotRemoveRequiredConnectionStringRedirection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var target = builder.AddRedis("target").WithLaunchGroups("backend");
        var redirect = builder.AddRedis("redirect").WithLaunchGroups("frontend")
            .WithOptionalLaunchGroupReference(target)
            .WithConnectionStringRedirection(target.Resource);
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend").WithReference(redirect);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'target', which is not included in launch group 'frontend'. " +
            "Dependency path: frontend -> redirect -> target. Edges: frontend --reference--> redirect; redirect --redirect--> target. " +
            "Add 'target' to launch group 'frontend'. The connection string redirection from 'redirect' to 'target' requires automatic startup.",
            exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStartErrorExplainsThatMembershipIsNotEnough(bool wait)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var dependency = builder.AddExecutable("dependency", "unused", ".")
            .WithLaunchGroups("frontend").WithExplicitStart();
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend").WithReferenceRelationship(dependency);
        if (wait)
        {
            frontend.WaitFor(dependency);
        }
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        var edgeGuidance = wait
            ? "The wait from 'frontend' to 'dependency' requires automatic startup."
            : "If the reference from 'frontend' to 'dependency' is optional, mark it with WithOptionalLaunchGroupReference.";
        Assert.Equal(
            "Resource 'frontend' requires local resource 'dependency', which is marked for explicit start. " +
            $"Dependency path: frontend -> dependency. Edges: frontend --{(wait ? "reference/wait" : "reference")}--> dependency. " +
            "Remove explicit start from 'dependency' and ensure it is included in launch group 'frontend'. " +
            edgeGuidance,
            exception.Message);
    }

    [Fact]
    public async Task TransitiveFailureReportsTheFullPathFromTheDependentRoot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var database = builder.AddExecutable("database", "unused", ".").WithLaunchGroups("backend");
        var api = builder.AddExecutable("api", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(database);
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(api);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Dependency path: frontend -> api -> database.", exception.Message);
        Assert.Contains("frontend --reference--> api", exception.Message);
        Assert.Contains("api --reference--> database", exception.Message);
    }

    [Fact]
    public async Task PathDistinguishesAnOptionalReferenceFromARequiredWait()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var target = builder.AddExecutable("target", "unused", ".").WithLaunchGroups("backend");
        var api = builder.AddExecutable("api", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithOptionalLaunchGroupReference(target)
            .WaitFor(target);
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(api);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Dependency path: frontend -> api -> target.", exception.Message);
        Assert.Contains("api --wait--> target", exception.Message);
        Assert.EndsWith("The wait from 'api' to 'target' requires automatic startup.", exception.Message);
    }

    [Fact]
    public async Task PathIncludesThePassiveDependencyAndItsLifetimeOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var cache = builder.AddExecutable("cache", "unused", ".").WithLaunchGroups("backend");
        var server = builder.AddPostgres("server")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(cache);
        var database = server.AddDatabase("database");
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReference(database);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Dependency path: frontend -> database -> server -> cache.", exception.Message);
        Assert.Contains("database --lifetime owner--> server", exception.Message);
    }

    [Fact]
    public async Task ReferenceCyclesStillReportTheReachableMissingDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var api = builder.AddExecutable("api", "unused", ".").WithLaunchGroups("frontend");
        var database = builder.AddExecutable("database", "unused", ".").WithLaunchGroups("backend");
        frontend.WithReferenceRelationship(api);
        api.WithReferenceRelationship(frontend).WithReferenceRelationship(database);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Dependency path: frontend -> api -> database.", exception.Message);
    }

    [Fact]
    public async Task SharedPathsAndCycleReportTheMissingEdgeWithoutMutatingResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var left = builder.AddExecutable("left", "unused", ".").WithLaunchGroups("frontend");
        var right = builder.AddExecutable("right", "unused", ".").WithLaunchGroups("frontend");
        var shared = builder.AddExecutable("shared", "unused", ".").WithLaunchGroups("frontend");
        var missing = builder.AddExecutable("missing", "unused", ".").WithLaunchGroups("backend");
        frontend.WithReferenceRelationship(left).WithReferenceRelationship(right);
        left.WithReferenceRelationship(shared);
        shared.WithReferenceRelationship(left);
        right.WithReferenceRelationship(shared).WithReferenceRelationship(missing);
        var argumentCallbacks = 0;
        var environmentCallbacks = 0;
        frontend.WithArgs(_ => Interlocked.Increment(ref argumentCallbacks))
            .WithEnvironment(_ => Interlocked.Increment(ref environmentCallbacks));
        IResource[] resources = [frontend.Resource, left.Resource, right.Resource, shared.Resource, missing.Resource];
        var annotationsBefore = new List<IResourceAnnotation[]>();
        // DCP initializes instance annotations before the pipeline, so capture the model before
        // BeforeStart completes and launch-group validation runs.
        builder.Pipeline.AddStep("capture-launch-group-annotations", _ =>
        {
            annotationsBefore.AddRange(resources.Select(resource => resource.Annotations.ToArray()));
            return Task.CompletedTask;
        }, requiredBy: WellKnownPipelineSteps.BeforeStart);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "Resource 'frontend' requires local resource 'missing', which is not included in launch group 'frontend'. " +
            "Dependency path: frontend -> right -> missing. Edges: frontend --reference--> right; right --reference--> missing. " +
            "Add 'missing' to launch group 'frontend'. " +
            "If the reference from 'right' to 'missing' is optional, mark it with WithOptionalLaunchGroupReference.",
            exception.Message);
        Assert.Equal(resources.Length, annotationsBefore.Count);
        for (var index = 0; index < resources.Length; index++)
        {
            Assert.Equal(annotationsBefore[index], resources[index].Annotations.ToArray());
        }
        Assert.Equal(0, argumentCallbacks);
        Assert.Equal(0, environmentCallbacks);
    }

    [Fact]
    public async Task PassiveLifetimeOwnerCyclesFailInsteadOfHanging()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var parent = builder.AddExecutable("parent", "unused", ".").WithLaunchGroups("frontend");
        var first = builder.AddResource(new ChildWithoutLifetime("first", parent.Resource)).WithLaunchGroups("frontend");
        var second = builder.AddResource(new ChildWithoutLifetime("second", first.Resource)).WithLaunchGroups("frontend");
        first.Resource.Parent = second.Resource;
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithReferenceRelationship(first);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("circular structural parent chain", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ChildExecutableResource(string name, IResource parent)
        : ExecutableResource(name, "unused", "."), IResourceWithParent
    {
        public IResource Parent { get; set; } = parent;
    }

    private sealed class ChildWithoutLifetime(string name, IResource parent)
        : Resource(name), IResourceWithParent, IResourceWithoutLifetime
    {
        public IResource Parent { get; set; } = parent;
    }
}
