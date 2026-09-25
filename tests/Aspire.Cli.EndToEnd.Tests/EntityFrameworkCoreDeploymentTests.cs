// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for publishing and deploying an EF Core migration bundle with Docker Compose.
/// </summary>
public sealed class EntityFrameworkCoreDeploymentTests(ITestOutputHelper output)
{
    private const string ProjectName = "EfCoreDeployTest";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployMigrationBundleToDockerCompose()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            mountDockerSocket: true,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal,
            workspace,
            auto,
            counter,
            output,
            TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.VerifyPullRequestCliVersionAsync(counter);

        await ScaffoldApplicationAsync(auto, counter, workspace);

        var appHostSha = Guid.NewGuid().ToString("N");
        var composeProject = $"aspire-compose-{appHostSha[..8]}";

        try
        {
            await auto.TypeAsync($"unset ASPIRE_PLAYGROUND; export AppHostSha={appHostSha}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // This is the boundary that regressed in #17905: the EF tool runs while the database
            // connection string is still a manifest placeholder, then the generated bundle image
            // receives the real connection string when Compose starts it.
            await auto.TypeAsync("aspire deploy -o deploy-output --non-interactive");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(10));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(1));

            await VerifyComposeArtifactsAsync(auto, counter);
            await VerifyDeploymentAsync(auto, counter, composeProject);

            await auto.AspireDestroyAsync(counter);
        }
        finally
        {
            // Aspire destroy is the primary cleanup path. This fallback also handles failures after
            // Compose has started but before deployment state has been persisted.
            await auto.TypeAsync(
                "if [ -f deploy-output/.env.Production ]; then " +
                $"docker compose --project-name {composeProject} --env-file deploy-output/.env.Production " +
                "-f deploy-output/docker-compose.yaml down --volumes --remove-orphans " +
                "|| echo 'WARNING: fallback compose cleanup failed'; fi");
            await auto.EnterAsync();
            await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromMinutes(2));
        }
    }

    private static async Task ScaffoldApplicationAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace)
    {
        await auto.AspireNewAsync(ProjectName, counter, useRedisCache: false);

        await auto.TypeAsync($"cd {ProjectName}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        foreach (var package in new[]
        {
            "Aspire.Hosting.Docker",
            "Aspire.Hosting.EntityFrameworkCore",
            "Aspire.Hosting.PostgreSQL",
        })
        {
            await auto.TypeAsync($"aspire add {package}");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
        }

        await auto.TypeAsync(AspireCliShellCommandHelpers.GetDotnetAddPackageCommand(
            $"{ProjectName}.ApiService",
            "Aspire.Npgsql.EntityFrameworkCore.PostgreSQL"));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        // Microsoft.EntityFrameworkCore.Design must match the EF Core version brought in by the
        // Aspire integration. Selecting the current prerelease independently can cross major versions.
        await auto.TypeAsync(
            $"EF_VERSION=$(dotnet list {ProjectName}.ApiService package --include-transitive " +
            "--format json | jq -r '.projects[].frameworks[].transitivePackages[] " +
            "| select(.id == \"Microsoft.EntityFrameworkCore\") | .resolvedVersion' | head -1) && " +
            $"test -n \"$EF_VERSION\" && dotnet add {ProjectName}.ApiService package " +
            "Microsoft.EntityFrameworkCore.Design --version \"$EF_VERSION\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

        await auto.TypeAsync(
            "case \"$(uname -m)\" in " +
            "x86_64) echo linux-x64 ;; " +
            "aarch64|arm64) echo linux-arm64 ;; " +
            "*) echo \"Unsupported architecture: $(uname -m)\" >&2; exit 1 ;; " +
            "esac > .aspire-e2e-rid");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
        var targetRuntimePath = Path.Combine(projectDirectory, ".aspire-e2e-rid");
        var targetRuntime = File.ReadAllText(targetRuntimePath).Trim();
        File.Delete(targetRuntimePath);

        WriteApplicationFiles(workspace, targetRuntime);

        // A valid design-time connection string lets this setup command create the initial migration
        // without starting PostgreSQL. The publish pipeline deliberately receives no such override.
        await auto.TypeAsync(
            $"ConnectionStrings__db='Host=localhost;Database=design;Username=postgres;Password=postgres' " +
            $"dotnet tool exec dotnet-ef --yes -- migrations add Initial " +
            $"--project {ProjectName}.ApiService --startup-project {ProjectName}.ApiService " +
            "--context AppDbContext --output-dir Migrations");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));
    }

    private static void WriteApplicationFiles(TemporaryWorkspace workspace, string targetRuntime)
    {
        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName);
        var appHostDirectory = Path.Combine(projectDirectory, $"{ProjectName}.AppHost");
        var apiDirectory = Path.Combine(projectDirectory, $"{ProjectName}.ApiService");

        File.WriteAllText(
            Path.Combine(appHostDirectory, "AppHost.cs"),
            $$"""
            using Aspire.Hosting.Docker.Resources.ComposeNodes;
            using Aspire.Hosting.Docker.Resources.ServiceNodes;

            var builder = DistributedApplication.CreateBuilder(args);

            var postgres = builder.AddPostgres("postgres")
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.Healthcheck = new Healthcheck
                    {
                        Test = ["CMD-SHELL", "pg_isready -U postgres"],
                        Interval = "2s",
                        Timeout = "5s",
                        Retries = 30,
                        StartPeriod = "5s"
                    };
                });
            var database = postgres.AddDatabase("db");

            var api = builder.AddProject<Projects.{{ProjectName}}_ApiService>("server")
                .WithReference(database)
                .WithExternalHttpEndpoints();

            var migrations = api.AddEFMigrations("migrations", "AppDbContext")
                .WithReference(database)
                .WaitFor(database)
                // The startup project uses Microsoft.NET.Sdk.Web, so its framework-dependent
                // bundle requires Microsoft.AspNetCore.App rather than the runtime-only image.
                .PublishAsMigrationBundle(
                    targetRuntime: "{{targetRuntime}}",
                    publishContainer: true,
                    baseImage: "mcr.microsoft.com/dotnet/aspnet:10.0")
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.DependsOn["postgres"] = new ServiceDependency
                    {
                        Condition = "service_healthy"
                    };
                    service.Restart = "no";
                });

            api.WaitForCompletion(migrations);

            builder.AddDockerComposeEnvironment("compose");

            builder.Build().Run();
            """);

        File.WriteAllText(
            Path.Combine(apiDirectory, "Program.cs"),
            """
            using Microsoft.EntityFrameworkCore;

            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();
            builder.AddNpgsqlDbContext<AppDbContext>("db");

            var app = builder.Build();
            app.MapDefaultEndpoints();
            app.MapGet("/test-deployment", async (AppDbContext dbContext) =>
            {
                var marker = await dbContext.Markers.SingleAsync();
                return marker.Value == "migration-applied"
                    ? Results.Ok("PASSED: EF Core migration bundle applied")
                    : Results.Problem($"Unexpected marker '{marker.Value}'");
            });
            app.Run();

            public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
            {
                public DbSet<MigrationMarker> Markers => Set<MigrationMarker>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<MigrationMarker>().HasData(
                        new MigrationMarker { Id = 1, Value = "migration-applied" });
                }
            }

            public sealed class MigrationMarker
            {
                public int Id { get; set; }

                public required string Value { get; set; }
            }
            """);

        var apiProjectPath = Path.Combine(apiDirectory, $"{ProjectName}.ApiService.csproj");
        var apiProject = XDocument.Load(apiProjectPath, LoadOptions.PreserveWhitespace);
        var targetFrameworkProperty = apiProject
            .Descendants()
            .SingleOrDefault(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks");
        Assert.NotNull(targetFrameworkProperty);

        var propertyGroup = targetFrameworkProperty.Parent;
        Assert.NotNull(propertyGroup);

        var runtimeIdentifiersProperty = propertyGroup
            .Elements()
            .SingleOrDefault(element => element.Name.LocalName == "RuntimeIdentifiers");
        if (runtimeIdentifiersProperty is null)
        {
            propertyGroup.Add(new XElement(propertyGroup.Name.Namespace + "RuntimeIdentifiers", targetRuntime));
        }
        else
        {
            runtimeIdentifiersProperty.Value = targetRuntime;
        }

        apiProject.Save(apiProjectPath, SaveOptions.DisableFormatting);
    }

    private static async Task VerifyComposeArtifactsAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        // Check the exact deployment relationships rather than only proving that a Compose file exists.
        await auto.TypeAsync(
            "test -f deploy-output/docker-compose.yaml && " +
            "docker compose --env-file deploy-output/.env.Production " +
            "-f deploy-output/docker-compose.yaml config --format json | " +
            "jq -e '((.services.migrations.environment | type) == \"object\" and " +
            "(.services.migrations.environment | has(\"ConnectionStrings__db\")) or " +
            "((.services.migrations.environment | type) == \"array\" and " +
            "any(.services.migrations.environment[]; startswith(\"ConnectionStrings__db=\")))) and " +
            "(.services.migrations.depends_on.postgres.condition == \"service_healthy\") and " +
            "(.services.server.depends_on.migrations.condition == \"service_completed_successfully\")'");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
    }

    private static async Task VerifyDeploymentAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string composeProject)
    {
        await auto.TypeAsync(
            "migration_id=$(docker ps -aq " +
            $"--filter \"label=com.docker.compose.project={composeProject}\" " +
            "--filter \"label=com.docker.compose.service=migrations\" | head -1) && " +
            "test -n \"$migration_id\" && " +
            "test \"$(docker inspect -f '{{.State.ExitCode}}' \"$migration_id\")\" = 0");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        // Compose does not publish the API port to the outer test container. Join the server's
        // network namespace and retry until the application is accepting requests.
        await auto.TypeAsync(
            "server_id=$(docker ps -q " +
            $"--filter \"label=com.docker.compose.project={composeProject}\" " +
            "--filter \"label=com.docker.compose.service=server\" | head -1) && " +
            "test -n \"$server_id\" && " +
            "succeeded=false; " +
            "for i in $(seq 1 30); do " +
            "result=$(docker run --rm --network container:$server_id curlimages/curl:8.12.1 " +
            "-fsS http://localhost:8080/test-deployment 2>/dev/null || true); " +
            "echo \"$result\"; " +
            "if echo \"$result\" | grep -q 'PASSED: EF Core migration bundle applied'; then succeeded=true; break; fi; " +
            "sleep 5; done; test \"$succeeded\" = true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));
    }
}
