// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateManifestNpmTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesCompleteNpmClosureIncludingNestedScopedAndOptionalPackages()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var environment = CreateEnvironment(workspace);
        using var command = environment.CreateCommand();

        (await command.ExecuteAsync()).EnsureSuccessful();

        await Verifier.Verify(File.ReadAllText(environment.ManifestPath), "json").UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("dependencies", "changed-version")]
    [InlineData("dependencies", "missing-entry")]
    [InlineData("dependencies", "extra-entry")]
    [InlineData("devDependencies", "changed-version")]
    [InlineData("devDependencies", "missing-entry")]
    [InlineData("devDependencies", "extra-entry")]
    [InlineData("optionalDependencies", "changed-version")]
    [InlineData("optionalDependencies", "missing-entry")]
    [InlineData("optionalDependencies", "extra-entry")]
    [RequiresTools(["pwsh"])]
    public async Task RejectsPackageAndLockfileMismatch(string group, string mismatch)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var environment = CreateEnvironment(workspace);
        var lockPath = Path.Combine(environment.TemplateDirectory, "package-lock.json");
        var lockfile = JsonNode.Parse(File.ReadAllText(lockPath))!;
        var dependencies = lockfile["packages"]![""]![group]!.AsObject();
        var name = dependencies.First().Key;
        switch (mismatch)
        {
            case "changed-version":
                dependencies[name] = "^99.0.0";
                break;
            case "missing-entry":
                dependencies.Remove(name);
                break;
            case "extra-entry":
                dependencies.Add("unexpected", "1.0.0");
                break;
        }
        File.WriteAllText(lockPath, lockfile.ToJsonString());
        using var command = environment.CreateCommand();

        var result = await command.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"{group} in {lockPath} differs from", result.Output);
        Assert.Contains("update the npm lockfile", result.Output);
        Assert.False(File.Exists(environment.ManifestPath));
        Assert.False(Directory.Exists(environment.RestoreDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMissingOrUnsupportedLockfile(bool unsupportedVersion)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var environment = CreateEnvironment(workspace);
        var lockPath = Path.Combine(environment.TemplateDirectory, "package-lock.json");
        if (unsupportedVersion)
        {
            var lockfile = JsonNode.Parse(File.ReadAllText(lockPath))!;
            lockfile["lockfileVersion"] = 2;
            File.WriteAllText(lockPath, lockfile.ToJsonString());
        }
        else
        {
            File.Delete(lockPath);
        }
        using var command = environment.CreateCommand();

        var result = await command.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(unsupportedVersion ? "Expected npm lockfile version 3" : "Missing npm lockfile", result.Output);
        Assert.False(File.Exists(environment.ManifestPath));
    }

    private TemplateManifestTestEnvironment CreateEnvironment(TemporaryWorkspace workspace)
    {
        var environment = new TemplateManifestTestEnvironment(workspace, output);
        TemplateManifestTestEnvironment.CreatePackage(environment.BuiltFeed, "Local.NpmFixture", "1.0.0");
        File.WriteAllText(Path.Combine(environment.TemplateDirectory, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Local.NpmFixture" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(environment.TemplateDirectory, "package.json"), PackageJson);
        File.WriteAllText(Path.Combine(environment.TemplateDirectory, "package-lock.json"), PackageLockJson);
        return environment;
    }

    private const string PackageJson = """
        {
          "name": "fixture-app",
          "version": "0.0.0",
          "private": true,
          "dependencies": { "direct": "^1.0.0", "@scope/direct": "^2.0.0", "number-alias": "npm:is-number@7.0.0" },
          "devDependencies": { "dev-tool": "^4.0.0" },
          "optionalDependencies": { "optional-native": "^5.0.0" }
        }
        """;

    private const string PackageLockJson = """
        {
          "name": "fixture-app",
          "version": "0.0.0",
          "lockfileVersion": 3,
          "packages": {
            "": {
              "name": "fixture-app",
              "version": "0.0.0",
              "dependencies": { "direct": "^1.0.0", "@scope/direct": "^2.0.0", "number-alias": "npm:is-number@7.0.0" },
              "devDependencies": { "dev-tool": "^4.0.0" },
              "optionalDependencies": { "optional-native": "^5.0.0" }
            },
            "node_modules/direct": {
              "version": "1.2.0",
              "dependencies": { "shared": "1.0.0", "@scope/transitive": "3.0.0", "scope-alias": "npm:@real/package@6.0.0" }
            },
            "node_modules/@scope/direct": {
              "version": "2.1.0",
              "dependencies": { "shared": "2.0.0", "@scope/transitive": "3.0.0" }
            },
            "node_modules/shared": { "version": "1.0.0" },
            "node_modules/number-alias": { "name": "is-number", "version": "7.0.0" },
            "node_modules/is-number": { "version": "7.0.0" },
            "node_modules/direct/node_modules/scope-alias": { "name": "@real/package", "version": "6.0.0" },
            "node_modules/@scope/direct/node_modules/shared": { "version": "2.0.0" },
            "node_modules/direct/node_modules/@scope/transitive": { "version": "3.0.0" },
            "node_modules/@scope/direct/node_modules/@scope/transitive": { "version": "3.0.0" },
            "node_modules/dev-tool": {
              "version": "4.1.0",
              "dev": true,
              "dependencies": { "dev-transitive": "4.2.0" }
            },
            "node_modules/dev-transitive": { "version": "4.2.0", "dev": true },
            "node_modules/optional-native": {
              "version": "5.1.0",
              "optional": true,
              "optionalDependencies": { "platform-linux": "5.2.0", "platform-win": "5.2.0" }
            },
            "node_modules/platform-linux": { "version": "5.2.0", "optional": true, "os": ["linux"] },
            "node_modules/platform-win": { "version": "5.2.0", "optional": true, "os": ["win32"] }
          }
        }
        """;
}
