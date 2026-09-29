// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.Pipelines;

public sealed class DashboardPackageSigningTests(ITestOutputHelper output)
{
    private const string StageLinuxStep = "🟣Stage Linux Native AOT Dashboard packages for signing";
    private const string StageRemainingStep = "🟣Stage Native AOT Dashboard packages";
    private static readonly string[] s_rids =
    [
        "linux-arm64", "linux-musl-x64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"
    ];

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StagesLinuxBeforeSigningAndPreservesSignedPackagesAfterward()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var shipping = workspace.CreateDirectory("artifacts/packages/Release/Shipping").FullName;
        CreateNativeArtifacts(workspace, s_rids);
        var steps = ReadAssembleSteps();

        var stageLinuxIndex = FindStep(steps, StageLinuxStep);
        var signIndex = FindStep(steps, "🟣Sign npm package tarballs");
        var stageRemainingIndex = FindStep(steps, StageRemainingStep);
        var verifyIndex = FindStep(steps, "🟣Verify Native AOT Dashboard package signatures");
        var publishIndex = FindStep(steps, "🟣Publish unified asset manifest");
        Assert.True(stageLinuxIndex < signIndex);
        Assert.True(signIndex < stageRemainingIndex);
        Assert.True(stageRemainingIndex < verifyIndex);
        Assert.True(verifyIndex < publishIndex);

        (await RunStep(workspace, steps[stageLinuxIndex])).EnsureSuccessful();
        var linuxRids = s_rids.Where(rid => rid.StartsWith("linux-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(linuxRids.Select(PackageName), Directory.GetFiles(shipping).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Simulate the signing pass changing package bytes; later staging must keep
        // those bytes rather than copy the unsigned source over them.
        foreach (var rid in linuxRids)
        {
            File.WriteAllText(Path.Combine(shipping, PackageName(rid)), $"signed {rid}");
        }

        (await RunStep(workspace, steps[stageRemainingIndex])).EnsureSuccessful();
        Assert.Equal(s_rids.Select(PackageName), Directory.GetFiles(shipping).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var rid in s_rids)
        {
            var expected = rid.StartsWith("linux-", StringComparison.Ordinal) ? $"signed {rid}" : $"original {rid}";
            Assert.Equal(expected, File.ReadAllText(Path.Combine(shipping, PackageName(rid))));
        }
    }

    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("linux-musl-x64")]
    [InlineData("linux-x64")]
    [RequiresTools(["pwsh"])]
    public async Task MissingLinuxPackageFailsBeforeSigning(string missingRid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var shipping = workspace.CreateDirectory("artifacts/packages/Release/Shipping").FullName;
        CreateNativeArtifacts(workspace, s_rids.Where(rid => rid != missingRid));
        var steps = ReadAssembleSteps();

        var result = await RunStep(workspace, steps[FindStep(steps, StageLinuxStep)]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected 3 Linux Native AOT Dashboard RID packages, found 2", result.Output);
        Assert.Empty(Directory.GetFiles(shipping));
    }

    private static void CreateNativeArtifacts(TemporaryWorkspace workspace, IEnumerable<string> rids)
    {
        foreach (var rid in rids)
        {
            var packages = workspace.CreateDirectory($"artifacts/native-cli-packages/Release/native_archives_{rid.Replace('-', '_')}/Release/Shipping");
            File.WriteAllText(Path.Combine(packages.FullName, PackageName(rid)), $"original {rid}");
            var archives = workspace.CreateDirectory($"artifacts/DashboardArtifacts/Release/{rid}");
            File.WriteAllText(Path.Combine(archives.FullName, $"aspire-dashboard-{rid}.zip"), $"archive {rid}");
        }
    }

    private async Task<CommandResult> RunStep(TemporaryWorkspace workspace, YamlMappingNode step)
    {
        var script = ((YamlScalarNode)step.Children[new YamlScalarNode("pwsh")]).Value!;
        script = script.Replace("$(Build.SourcesDirectory)", workspace.Path, StringComparison.Ordinal)
            .Replace("$(_BuildConfig)", "Release", StringComparison.Ordinal);
        var scriptPath = Path.Combine(workspace.Path, "step.ps1");
        await File.WriteAllTextAsync(scriptPath, script);
        using var command = new PowerShellCommand(scriptPath, output).WithTimeout(TimeSpan.FromMinutes(1));

        return await command.ExecuteAsync();
    }

    private static List<YamlMappingNode> ReadAssembleSteps()
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, "eng", "pipelines", "azure-pipelines.yml"));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var extends = (YamlMappingNode)root.Children[new YamlScalarNode("extends")];
        var parameters = (YamlMappingNode)extends.Children[new YamlScalarNode("parameters")];
        var stages = (YamlSequenceNode)parameters.Children[new YamlScalarNode("stages")];
        var assemble = stages.Cast<YamlMappingNode>().Single(stage =>
            stage.Children.TryGetValue(new YamlScalarNode("stage"), out var name) && ((YamlScalarNode)name).Value == "assemble");
        var template = (YamlMappingNode)((YamlSequenceNode)assemble.Children[new YamlScalarNode("jobs")]).Children.Single();
        var jobParameters = (YamlMappingNode)template.Children[new YamlScalarNode("parameters")];
        var job = (YamlMappingNode)((YamlSequenceNode)jobParameters.Children[new YamlScalarNode("jobs")]).Children.Single();

        return ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();
    }

    private static int FindStep(List<YamlMappingNode> steps, string displayName)
    {
        var index = steps.FindIndex(step =>
            step.Children.TryGetValue(new YamlScalarNode("displayName"), out var name) && ((YamlScalarNode)name).Value == displayName);
        Assert.True(index >= 0, $"Missing pipeline step: {displayName}");

        return index;
    }

    private static string PackageName(string rid) => $"Aspire.Dashboard.Sdk.{rid}.13.6.0.nupkg";
}
