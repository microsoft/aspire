// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Dcp;
using k8s;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public sealed class JsonPatchTests
{
    [Fact]
    public void Create_ObjectDifferences_CreatesAddRemoveAndReplaceOperations()
    {
        var current = JsonNode.Parse("""{"unchanged":1,"removed":2,"replaced":"before"}""");
        var changed = JsonNode.Parse("""{"unchanged":1,"replaced":"after","added":true}""");

        var patch = JsonPatch.Create(current, changed);

        Assert.Equal(
            """[{"op":"remove","path":"/removed"},{"op":"replace","path":"/replaced","value":"after"},{"op":"add","path":"/added","value":true}]""",
            KubernetesJson.Serialize(patch, null));
        Assert.True(JsonNode.DeepEquals(changed, JsonPatch.Apply(current, patch)));
    }

    [Fact]
    public void Create_ScalarDifference_CreatesRootReplaceOperation()
    {
        var current = JsonValue.Create("before");
        var changed = JsonValue.Create("after");

        var patch = JsonPatch.Create(current, changed);

        Assert.Equal(
            """[{"op":"replace","path":"","value":"after"}]""",
            KubernetesJson.Serialize(patch, null));
        Assert.True(JsonNode.DeepEquals(changed, JsonPatch.Apply(current, patch)));
    }

    [Theory]
    [InlineData(
        """[1,2]""",
        """[1,2,3,4]""",
        """[{"op":"add","path":"/2","value":3},{"op":"add","path":"/3","value":4}]""")]
    [InlineData(
        """[1,2,3,4]""",
        """[1,2]""",
        """[{"op":"remove","path":"/3"},{"op":"remove","path":"/2"}]""")]
    [InlineData(
        """[{"value":"before"},2]""",
        """[{"value":"after"},3]""",
        """[{"op":"replace","path":"/0/value","value":"after"},{"op":"replace","path":"/1","value":3}]""")]
    public void Create_ArrayDifferences_CreatesApplicableOperations(string currentJson, string changedJson, string expectedPatchJson)
    {
        var current = JsonNode.Parse(currentJson);
        var changed = JsonNode.Parse(changedJson);

        var patch = JsonPatch.Create(current, changed);

        Assert.Equal(expectedPatchJson, KubernetesJson.Serialize(patch, null));
        Assert.True(JsonNode.DeepEquals(changed, JsonPatch.Apply(current, patch)));
    }

    [Fact]
    public void Create_PropertyNamesWithJsonPointerCharacters_EscapesPathSegments()
    {
        var current = JsonNode.Parse("""{"a/b":{"m~n":"before"}}""");
        var changed = JsonNode.Parse("""{"a/b":{"m~n":"after"}}""");

        var patch = JsonPatch.Create(current, changed);

        Assert.Equal(
            """[{"op":"replace","path":"/a~1b/m~0n","value":"after"}]""",
            KubernetesJson.Serialize(patch, null));
        Assert.True(JsonNode.DeepEquals(changed, JsonPatch.Apply(current, patch)));
    }
}
