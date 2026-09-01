// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.Pipelines;

/// <summary>
/// Reads <c>.github/workflows/extension-e2e-tests.yml</c> so the tests that pin its contracts all
/// resolve the same job and steps.
/// </summary>
internal static class ExtensionE2eWorkflow
{
    private const string WorkflowRelativePath = ".github/workflows/extension-e2e-tests.yml";

    public static YamlMappingNode Job()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, WorkflowRelativePath)));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];

        return (YamlMappingNode)jobs.Children[new YamlScalarNode("extension_e2e")];
    }

    public static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job)
        => ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>();

    /// <summary>
    /// Returns the <c>run:</c> body of the named step, so a test can execute the real script rather
    /// than a copy that drifts from it.
    /// </summary>
    public static string StepScript(string stepName)
    {
        var step = Steps(Job()).Single(candidate => Scalar(candidate, "name") == stepName);

        return Scalar(step, "run") ?? throw new InvalidOperationException($"Step '{stepName}' has no run script.");
    }

    public static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;
}
