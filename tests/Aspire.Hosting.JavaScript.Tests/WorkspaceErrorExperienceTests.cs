// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREJAVASCRIPT001

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript.Internal.Workspace;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.JavaScript.Tests;

/// <summary>
/// Exercises every API-misuse and pipeline-time configuration error path and verifies the
/// user-facing exception message. This file doubles as the source-of-truth catalog for
/// "what does the user see when they configure the workspace incorrectly" — when a test
/// here fails, it is because we changed an error message that someone depends on, and
/// the new message should be reviewed for clarity before snapshot is updated.
/// </summary>
public class WorkspaceErrorExperienceTests
{
    // -----------------------------------------------------------------------
    // API-misuse exceptions: thrown synchronously from the user's AppHost code.
    // -----------------------------------------------------------------------

    [Fact]
    public void DoubleWithWorkspaceRoot_Throws_InvalidOperationException()
    {
        using var tempDir = new TestTempDirectory();
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        Directory.CreateDirectory(appDir);

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<InvalidOperationException>(() => node.WithWorkspaceRoot(tempDir.Path));

        Assert.Contains("already has a workspace root", ex.Message);
        Assert.Contains("WithWorkspaceRoot can only be called once", ex.Message);
    }

    [Fact]
    public void DoublePublishAs_Throws_InvalidOperationException()
    {
        using var tempDir = new TestTempDirectory();
        var appDir = Path.Combine(tempDir.Path, "app");
        Directory.CreateDirectory(appDir);

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish)
            .WithResourceCleanUp(true);

        var node = builder.AddViteApp("web", appDir);

        // AddViteApp does not set a publish mode by itself; the second PublishAs* should throw.
        node.PublishAsStaticWebsite();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            node.PublishAsNpmScript("start"));

        Assert.Contains("already has a publish mode set by PublishAsStaticWebsite", ex.Message);
        Assert.Contains("PublishAsNpmScript cannot also be applied", ex.Message);
    }

    [Fact]
    public void PublishAsStaticWebsite_AfterAddNextJsApp_Throws_InvalidOperationException()
    {
        using var tempDir = new TestTempDirectory();
        var appDir = Path.Combine(tempDir.Path, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "package.json"),
            """{"name":"web","scripts":{"build":"next build","start":"next start","dev":"next dev"}}""");
        File.WriteAllText(Path.Combine(appDir, "next.config.js"),
            "module.exports = { output: \"standalone\" };");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish)
            .WithResourceCleanUp(true);

        var next = builder.AddNextJsApp("web", appDir);

        var ex = Assert.Throws<InvalidOperationException>(() => next.PublishAsStaticWebsite());

        Assert.Contains("already has a publish mode set by AddNextJsApp", ex.Message);
        Assert.Contains("PublishAsStaticWebsite cannot also be applied", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Configuration errors: thrown by the validator, surfaced to the user as
    // a single DistributedApplicationException listing every issue at once.
    // These would surface during `aspire publish` / `aspire do build` or at
    // run-mode resource startup — never from the AppHost constructor.
    // -----------------------------------------------------------------------

    [Fact]
    public void Config_NonExistentRoot_DoesNotThrowAtCallSite()
    {
        // The classic test that this MUST NOT throw at API time.
        using var tempDir = new TestTempDirectory();
        var appDir = Path.Combine(tempDir.Path, "app");
        Directory.CreateDirectory(appDir);
        WriteAppPackageJson(appDir, "@example/app");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // The user can call WithWorkspaceRoot with a bogus path and the AppHost still constructs.
        // The error surfaces at validation time, not here.
        var node = builder.AddNodeApp("app", appDir, "app.js")
            .WithWorkspaceRoot(Path.Combine(tempDir.Path, "does-not-exist"));

        Assert.NotNull(node);
    }

    [Fact]
    public void Config_NonExistentRoot_ValidatorThrows()
    {
        using var tempDir = new TestTempDirectory();
        var appDir = Path.Combine(tempDir.Path, "app");
        Directory.CreateDirectory(appDir);
        WriteAppPackageJson(appDir, "@example/app");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("app", appDir, "app.js")
            .WithWorkspaceRoot(Path.Combine(tempDir.Path, "does-not-exist"));

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "app", "does not exist");
    }

    [Fact]
    public void Config_MalformedPackageJson_ValidatorReportsFile()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{ not valid json,");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "Failed to parse", "package.json");
    }

    [Fact]
    public void Config_MalformedPnpmYaml_ValidatorReportsFile()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            "{\"name\":\"root\",\"private\":true}");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"),
            "packages:\n  - 'apps/*\n"); // unbalanced quote
        var appDir = Path.Combine(tempDir.Path, "apps", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "Failed to parse", "pnpm-workspace.yaml");
    }

    [Fact]
    public void Config_AppDirectoryIsWorkspaceRoot_ValidatorReportsMemberSubdirectoryRequirement()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"@example/web","private":true,"workspaces":["packages/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", tempDir.Path, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "is the workspace root", "workspace member subdirectory");
    }

    [Fact]
    public void Config_AppMissingPackageJson_ValidatorReportsMissingPackageJson()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":["packages/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        Directory.CreateDirectory(appDir);

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "is missing", "package.json", "with a 'name' field");
    }

    [Fact]
    public void Config_RootMissingPackageJson_ValidatorReportsMissingRootManifest()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"),
            "packages:\n  - 'packages/*'\n");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "Workspace root", "is missing package.json");
    }

    [Fact]
    public void Config_NoWorkspacePatterns_ValidatorReportsMissingWorkspaceDeclaration()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "No workspace patterns declared", "workspaces", "packages");
    }

    [Fact]
    public void Config_InvalidWorkspacesShape_ValidatorReportsShape()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":{"foo":"bar"}}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "object must contain a 'packages' array", "found keys: foo");
    }

    [Fact]
    public void Config_WorkspacesNull_ValidatorReportsShape()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":null}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "workspaces", "is null", "string array");
    }

    [Fact]
    public void Config_WorkspacesPackagesNull_ValidatorReportsShape()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":{"packages":null}}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "workspaces.packages", "is null", "string array");
    }

    [Fact]
    public void Config_PnpmYamlPackagesNull_ValidatorReportsShape()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            "{\"name\":\"root\",\"private\":true}");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"), "packages: ~\n");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "is null", "Expected a YAML sequence of strings");
    }

    [Fact]
    public void Config_PnpmYamlPackagesScalar_ValidatorReportsFile()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            "{\"name\":\"root\",\"private\":true}");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"), "packages: 'packages/*'\n");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "Failed to parse", "pnpm-workspace.yaml");
    }

    [Fact]
    public void Config_PatternMatchesNoPackageDirs_ValidatorReportsPattern()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":["packages/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        // Create a 'packages/' dir but with a child that has NO package.json (e.g. a docs folder).
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "packages", "docs"));
        File.WriteAllText(Path.Combine(tempDir.Path, "packages", "docs", "README.md"), "# docs");
        // The app the user is configuring lives at apps/web — does not match the pattern.
        var appDir = Path.Combine(tempDir.Path, "apps", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "Workspace pattern", "'packages/*'", "did not match any directory containing a package.json");
    }

    [Fact]
    public void Config_DuplicatePackageNames_ValidatorReportsBoth()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":["apps/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        // Two members both declaring "name": "web"
        var webDir = Path.Combine(tempDir.Path, "apps", "web");
        var legacyWebDir = Path.Combine(tempDir.Path, "apps", "legacy-web");
        WriteAppPackageJson(webDir, "web");
        WriteAppPackageJson(legacyWebDir, "web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", webDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "Workspace package name 'web' is declared by both", "apps/legacy-web/package.json", "apps/web/package.json");
    }

    [Fact]
    public void Config_PackageManagerVsLockfile_ValidatorReportsConflict()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":["packages/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"),
            "packages:\n  - 'packages/*'\n");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // User configured npm but the workspace clearly uses pnpm.
        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path)
            .WithNpm();

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "has a pnpm lockfile", "configured to use npm", "Either call .WithPnpm()");
        AssertValidationMessage(ex, "web", "pnpm-workspace.yaml", "pnpm-specific");
    }

    [Fact]
    public void Config_PackageManagerVsPackageManagerField_ValidatorReportsConflict()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"packageManager":"yarn@4.0.0","workspaces":["packages/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web");

        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var node = builder.AddNodeApp("web", appDir, "app.js")
            .WithWorkspaceRoot(tempDir.Path)
            .WithNpm();

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "package.json#packageManager' is 'yarn@4.0.0'", "configured to use npm", "call .WithYarn()");
    }

    [Fact]
    public void Config_PnpmPublishAsNpmScriptWithoutInjectedWorkspacePackages_ValidatorReportsPnpm10Requirement()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"packageManager":"pnpm@10.33.4"}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "");
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-workspace.yaml"),
            "packages:\n  - 'packages/*'\n");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web", scripts: """{"start":"node index.js"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish)
            .WithResourceCleanUp(true);

        var node = builder.AddJavaScriptApp("web", appDir, runScriptName: "start")
            .WithWorkspaceRoot(tempDir.Path)
            .WithPnpm()
            .PublishAsNpmScript("start");

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(node.Resource));

        AssertValidationMessage(ex, "web", "pnpm 10 Dockerfile", "injectWorkspacePackages: true", "without legacy deploy mode");
    }

    [Fact]
    public void Config_MissingBuildScript_ValidatorReportsScriptList()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":["packages/*"]}""");
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "{}");
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        // User declares scripts but not 'build' — the auto-attached WithBuildScript("build")
        // by AddViteApp will then fail.
        WriteAppPackageJson(appDir, "@example/web", scripts: """{"dev":"vite","start":"vite preview"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish)
            .WithResourceCleanUp(true);

        var vite = builder.AddViteApp("web", appDir)
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(vite.Resource));

        AssertValidationMessage(ex, "web", "references script 'build'", "does not declare 'scripts.build'", "Declared scripts: dev, start");
    }

    [Fact]
    public void Config_MultipleErrorsCollectedAtOnce_OneExceptionPerCall()
    {
        // The validator surfaces every problem at once so the user can fix them in one pass
        // instead of round-tripping through `aspire publish` per fix.
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"),
            """{"name":"root","private":true,"workspaces":["packages/*"]}""");
        // No lockfile.
        var appDir = Path.Combine(tempDir.Path, "packages", "web");
        WriteAppPackageJson(appDir, "@example/web", scripts: """{"start":"node app.js"}""");

        using var builder = TestDistributedApplicationBuilder
            .Create(DistributedApplicationOperation.Publish)
            .WithResourceCleanUp(true);

        var vite = builder.AddViteApp("web", appDir)
            .WithWorkspaceRoot(tempDir.Path);

        var ex = Assert.Throws<DistributedApplicationException>(() =>
            WorkspaceConfigurationValidator.ValidateAndAttach(vite.Resource));

        // Three issues should be reported in the same exception.
        Assert.Contains("no recognized lockfile", ex.Message);
        Assert.Contains("references script 'build'", ex.Message);
        // Make sure it's a single DistributedApplicationException, not a chain.
        Assert.Null(ex.InnerException);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void AssertValidationMessage(DistributedApplicationException ex, string resourceName, params string[] expectedSubstrings)
    {
        Assert.Contains($"resource '{resourceName}'", ex.Message);
        foreach (var s in expectedSubstrings)
        {
            Assert.Contains(s, ex.Message);
        }
    }

    private static void WriteAppPackageJson(string appDir, string name, string? scripts = null)
    {
        Directory.CreateDirectory(appDir);
        var payload = scripts is null
            ? "{\"name\":\"" + name + "\"}"
            : "{\"name\":\"" + name + "\",\"scripts\":" + scripts + "}";
        File.WriteAllText(Path.Combine(appDir, "package.json"), payload);
    }
}

/// <summary>
/// Captures the user-facing error message for every error scenario into a single text file.
/// This is run as a test and intentionally produces a snapshot-like output that can be
/// inspected by the maintainer when reviewing UX for the JS workspace feature.
/// </summary>
public class WorkspaceErrorExperienceSnapshotTests
{
    [Fact]
    public void CaptureUserExperience_WritesAllErrorMessagesToOutput()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# JavaScript workspace error UX catalog");
        sb.AppendLine();
        sb.AppendLine("This file is generated by `WorkspaceErrorExperienceSnapshotTests.CaptureUserExperience_WritesAllErrorMessagesToOutput`.");
        sb.AppendLine("Each section shows the exact text the user sees for a single misconfiguration.");
        sb.AppendLine();

        AppendApiMisuse(sb, "Double WithWorkspaceRoot",
            () =>
            {
                using var tempDir = new TestTempDirectory();
                var appDir = Path.Combine(tempDir.Path, "packages", "web");
                Directory.CreateDirectory(appDir);

                using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
                var node = builder.AddNodeApp("web", appDir, "app.js").WithWorkspaceRoot(tempDir.Path);
                node.WithWorkspaceRoot(tempDir.Path);
            });

        AppendApiMisuse(sb, "Double PublishAs*",
            () =>
            {
                using var tempDir = new TestTempDirectory();
                var appDir = Path.Combine(tempDir.Path, "app");
                Directory.CreateDirectory(appDir);
                using var builder = TestDistributedApplicationBuilder
                    .Create(DistributedApplicationOperation.Publish)
                    .WithResourceCleanUp(true);
                var vite = builder.AddViteApp("web", appDir);
                vite.PublishAsStaticWebsite();
                vite.PublishAsNpmScript("start");
            });

        AppendValidator(sb, "Workspace root does not exist",
            (scenarioRoot, app) => app.WithWorkspaceRoot("/path/that/does/not/exist"));

        AppendValidator(sb, "Malformed package.json",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"), "{ not valid,");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "Application directory is workspace root",
            (scenarioRoot, app) =>
            {
                File.WriteAllText(Path.Combine(scenarioRoot, "package.json"),
                    """{"name":"@example/web","private":true,"workspaces":["packages/*"]}""");
                File.WriteAllText(Path.Combine(scenarioRoot, "package-lock.json"), "{}");
                app.WithWorkspaceRoot(scenarioRoot);
            },
            appDirRelative: string.Empty);

        AppendValidator(sb, "Application package.json is missing",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":["packages/*"]}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                File.Delete(Path.Combine(root, "ws", "packages", "web", "package.json"));
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "Workspace root package.json is missing",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-lock.yaml"), "");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-workspace.yaml"),
                    "packages:\n  - 'packages/*'\n");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "No workspace patterns declared",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "package.json workspaces is null",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":null}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "Workspace declared as object without 'packages'",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":{"foo":"bar"}}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "Workspace object packages is null",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":{"packages":null}}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "pnpm-workspace.yaml packages is null",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"packageManager":"pnpm@10.33.4"}""");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-lock.yaml"), "");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-workspace.yaml"), "packages: ~\n");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "pnpm-workspace.yaml packages is scalar",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true}""");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-lock.yaml"), "");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-workspace.yaml"), "packages: 'packages/*'\n");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "Pattern matched directories but none have package.json",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":["packages/*"]}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "docs"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "docs", "README.md"), "# docs");
                Directory.CreateDirectory(Path.Combine(root, "ws", "apps", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "apps", "web", "package.json"),
                    """{"name":"@example/web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/apps/web");

        AppendValidator(sb, "Duplicate package names",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":["apps/*"]}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                Directory.CreateDirectory(Path.Combine(root, "ws", "apps", "web"));
                Directory.CreateDirectory(Path.Combine(root, "ws", "apps", "legacy-web"));
                File.WriteAllText(Path.Combine(root, "ws", "apps", "web", "package.json"), """{"name":"web"}""");
                File.WriteAllText(Path.Combine(root, "ws", "apps", "legacy-web", "package.json"), """{"name":"web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/apps/web",
            appPackageName: "web");

        AppendValidator(sb, "PM mismatch: WithNpm but pnpm-lock.yaml present",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":["packages/*"]}""");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-lock.yaml"), "");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-workspace.yaml"),
                    "packages:\n  - 'packages/*'\n");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws")).WithNpm();
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "PM mismatch: packageManager field is yarn but WithNpm",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"packageManager":"yarn@4.0.0","workspaces":["packages/*"]}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web"}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws")).WithNpm();
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "pnpm PublishAsNpmScript missing injectWorkspacePackages",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"packageManager":"pnpm@10.33.4"}""");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-lock.yaml"), "");
                File.WriteAllText(Path.Combine(root, "ws", "pnpm-workspace.yaml"),
                    "packages:\n  - 'packages/*'\n");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web","scripts":{"start":"node index.js"}}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws")).WithPnpm().PublishAsNpmScript("start");
            },
            appDirRelative: "ws/packages/web");

        AppendValidator(sb, "Missing 'build' script in package.json#scripts",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":["packages/*"]}""");
                File.WriteAllText(Path.Combine(root, "ws", "package-lock.json"), "{}");
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web","scripts":{"dev":"vite","start":"vite preview"}}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web",
            useViteApp: true);

        AppendValidator(sb, "Multiple errors at once",
            (scenarioRoot, app) =>
            {
                var root = scenarioRoot;
                Directory.CreateDirectory(Path.Combine(root, "ws"));
                File.WriteAllText(Path.Combine(root, "ws", "package.json"),
                    """{"name":"r","private":true,"workspaces":["packages/*"]}""");
                // No lockfile, AND member missing 'build' script.
                Directory.CreateDirectory(Path.Combine(root, "ws", "packages", "web"));
                File.WriteAllText(Path.Combine(root, "ws", "packages", "web", "package.json"),
                    """{"name":"@example/web","scripts":{"dev":"vite"}}""");
                app.WithWorkspaceRoot(Path.Combine(root, "ws"));
            },
            appDirRelative: "ws/packages/web",
            useViteApp: true);

        var output = sb.ToString();

        // Write to test output dir for human inspection.
        var outputDir = Path.Combine(AppContext.BaseDirectory, "WorkspaceErrorExperience");
        Directory.CreateDirectory(outputDir);
        var outputFile = Path.Combine(outputDir, "error-catalog.md");
        File.WriteAllText(outputFile, output);

        // The test's assertion: the catalog must mention every category the user might encounter.
        // If a section is missing, someone removed an error path without updating the snapshot.
        Assert.Contains("Double WithWorkspaceRoot", output);
        Assert.Contains("Double PublishAs*", output);
        Assert.Contains("Workspace root does not exist", output);
        Assert.Contains("Malformed package.json", output);
        Assert.Contains("Application directory is workspace root", output);
        Assert.Contains("Application package.json is missing", output);
        Assert.Contains("Workspace root package.json is missing", output);
        Assert.Contains("No workspace patterns declared", output);
        Assert.Contains("package.json workspaces is null", output);
        Assert.Contains("Workspace object packages is null", output);
        Assert.Contains("pnpm-workspace.yaml packages is null", output);
        Assert.Contains("pnpm-workspace.yaml packages is scalar", output);
        Assert.Contains("Pattern matched directories but none have package.json", output);
        Assert.Contains("Duplicate package names", output);
        Assert.Contains("PM mismatch", output);
        Assert.Contains("Missing 'build' script", output);
        Assert.Contains("Multiple errors at once", output);
    }

    private static void AppendApiMisuse(StringBuilder sb, string title, Action runner)
    {
        sb.Append("## API misuse: ").AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("```");
        try
        {
            runner();
            sb.AppendLine("(no exception thrown — TEST BUG)");
        }
        catch (Exception ex)
        {
            sb.Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);
        }
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void AppendValidator(
        StringBuilder sb,
        string title,
        Action<string, IResourceBuilder<JavaScriptAppResource>> setup,
        string? appDirRelative = null,
        string? appPackageName = null,
        bool useViteApp = false)
    {
        sb.Append("## Validator: ").AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("```");
        try
        {
            // Use a unique temp directory per scenario so state from one scenario does not leak
            // into the next. We also make the catalog deterministic by replacing the temp
            // directory path with a fixed token in the output.
            using var scenarioTemp = new TestTempDirectory();
            using var builder = TestDistributedApplicationBuilder
                .Create(DistributedApplicationOperation.Publish)
                .WithResourceCleanUp(true);

            var resolvedAppDir = appDirRelative is null
                ? Path.Combine(scenarioTemp.Path, "app")
                : Path.Combine(scenarioTemp.Path, appDirRelative);

            // Ensure the app dir exists and has a minimal package.json so the WithWorkspaceRoot
            // call has somewhere to point.
            Directory.CreateDirectory(resolvedAppDir);
            if (!File.Exists(Path.Combine(resolvedAppDir, "package.json")))
            {
                File.WriteAllText(Path.Combine(resolvedAppDir, "package.json"),
                    appPackageName is null
                        ? """{"name":"@example/web"}"""
                        : $"{{\"name\":\"{appPackageName}\"}}");
            }

            IResourceBuilder<JavaScriptAppResource> app = useViteApp
                ? builder.AddViteApp("web", resolvedAppDir)
                : builder.AddNodeApp("web", resolvedAppDir, "app.js");

            setup(scenarioTemp.Path, app);

            WorkspaceConfigurationValidator.ValidateAndAttach(app.Resource);
            sb.AppendLine("(no exception thrown — TEST BUG)");
        }
        catch (DistributedApplicationException ex)
        {
            sb.AppendLine(ScrubPath(ex.Message));
        }
        catch (Exception ex)
        {
            sb.Append(ex.GetType().Name).Append(": ").AppendLine(ScrubPath(ex.Message));
        }
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static string ScrubPath(string message)
    {
        // Replace OS temp directory paths with <TEMP> so the catalog is deterministic.
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        message = message.Replace(temp, "<TEMP>", StringComparison.Ordinal);
        return message;
    }
}
