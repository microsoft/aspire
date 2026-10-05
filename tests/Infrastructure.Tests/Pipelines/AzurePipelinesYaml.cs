// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.RepresentationModel;
using Xunit;

namespace Infrastructure.Tests;

internal static class AzurePipelinesYaml
{
    public static YamlMappingNode Load(string relativePath)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, relativePath)));
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    public static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    public static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    public static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;

    public static IReadOnlyList<YamlMappingNode> Parameters(YamlMappingNode root)
        => Sequence(root, "parameters").Cast<YamlMappingNode>().ToArray();

    public static YamlMappingNode Parameter(YamlMappingNode root, string name)
        => Assert.Single(Parameters(root), parameter => Scalar(parameter, "name") == name);

    public static IReadOnlyList<YamlMappingNode> Variables(YamlMappingNode root)
        => Sequence(root, "variables").Cast<YamlMappingNode>().ToArray();

    public static YamlMappingNode Variable(YamlMappingNode root, string name)
        => Assert.Single(Variables(root), variable => Scalar(variable, "name") == name);

    public static IReadOnlyList<YamlMappingNode> Stages(YamlMappingNode root)
        => Sequence(Mapping(Mapping(root, "extends"), "parameters"), "stages").Cast<YamlMappingNode>().ToArray();

    public static YamlMappingNode Stage(YamlMappingNode root, string name)
        => Assert.Single(Stages(root), stage => Scalar(stage, "stage") == name);

    public static IReadOnlyList<YamlMappingNode> Jobs(YamlMappingNode stage)
        => Sequence(stage, "jobs").Cast<YamlMappingNode>().ToArray();

    public static IReadOnlyList<YamlMappingNode> JobsRecursively(YamlMappingNode stage)
        => FlattenMappings(Sequence(stage, "jobs")).ToArray();

    public static YamlMappingNode Job(YamlMappingNode stage, string name)
        => Assert.Single(JobsRecursively(stage), job => Scalar(job, "job") == name);

    public static IReadOnlyList<YamlMappingNode> Steps(YamlMappingNode job)
        => Sequence(job, "steps").Cast<YamlMappingNode>().ToArray();

    public static IReadOnlyList<YamlMappingNode> StepsRecursively(YamlMappingNode job)
        => FlattenMappings(Sequence(job, "steps")).ToArray();

    public static YamlMappingNode Step(YamlMappingNode job, string displayName)
        => Assert.Single(StepsRecursively(job), step => Scalar(step, "displayName") == displayName);

    private static IEnumerable<YamlMappingNode> FlattenMappings(YamlSequenceNode sequence)
    {
        foreach (var node in sequence)
        {
            foreach (var mapping in FlattenMappings(node))
            {
                yield return mapping;
            }
        }
    }

    private static IEnumerable<YamlMappingNode> FlattenMappings(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            yield return mapping;
            foreach (var child in mapping.Children.Values)
            {
                foreach (var nestedMapping in FlattenMappings(child))
                {
                    yield return nestedMapping;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence)
            {
                foreach (var nestedMapping in FlattenMappings(child))
                {
                    yield return nestedMapping;
                }
            }
        }
    }
}
