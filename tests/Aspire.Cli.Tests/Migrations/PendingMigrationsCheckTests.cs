// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Migrations;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Migrations;

public class PendingMigrationsCheckTests
{
    [Fact]
    public void Order_Is102()
    {
        var check = new PendingMigrationsCheck([], NullLogger<PendingMigrationsCheck>.Instance);

        Assert.Equal(102, check.Order);
    }

    [Fact]
    public async Task CheckAsync_WithNoMigrations_ReturnsEmpty()
    {
        var check = new PendingMigrationsCheck([], NullLogger<PendingMigrationsCheck>.Instance);

        var results = await check.CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithApplicableMigration_ReturnsWarning()
    {
        var descriptor = new MigrationDescriptor
        {
            Title = "Migrate the thing",
            Detail = "The thing needs migrating",
            Metadata = new JsonObject { ["language"] = "typescript" }
        };
        var migration = new StubMigration("stub-migration", 100, descriptor);
        var check = new PendingMigrationsCheck([migration], NullLogger<PendingMigrationsCheck>.Instance);

        var results = await check.CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal(EnvironmentCheckCategories.AppHost, result.Category);
        Assert.Equal("stub-migration", result.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("The thing needs migrating", result.Message);
        Assert.NotNull(result.Fix);
        Assert.Same(descriptor.Metadata, result.Metadata);
    }

    [Fact]
    public async Task CheckAsync_WithNonApplicableMigration_ReturnsEmpty()
    {
        var migration = new StubMigration("stub-migration", 100, descriptor: null);
        var check = new PendingMigrationsCheck([migration], NullLogger<PendingMigrationsCheck>.Instance);

        var results = await check.CheckAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task CheckAsync_WithFailingMigration_SkipsItAndContinues()
    {
        var failing = new StubMigration("failing", 100, descriptor: null, throwOnDetect: true);
        var applicable = new StubMigration("applicable", 200, new MigrationDescriptor
        {
            Title = "Apply me",
            Detail = "Detail message"
        });
        var check = new PendingMigrationsCheck([failing, applicable], NullLogger<PendingMigrationsCheck>.Instance);

        var results = await check.CheckAsync();

        var result = Assert.Single(results);
        Assert.Equal("applicable", result.Name);
    }

    private sealed class StubMigration(string id, int order, MigrationDescriptor? descriptor, bool throwOnDetect = false) : IMigration
    {
        public string Id => id;

        public int Order => order;

        public Task<MigrationDescriptor?> DetectAsync(CancellationToken cancellationToken)
        {
            if (throwOnDetect)
            {
                throw new InvalidOperationException("Detection failed");
            }

            return Task.FromResult(descriptor);
        }

        public Task ApplyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
