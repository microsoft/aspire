// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.Tests.Configuration;

public class YamlJsonConverterTests
{
    [Fact]
    public void YamlToJson_ConvertsSimpleMapping()
    {
        var yaml = """
            key1: value1
            key2: value2
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.Equal("value1", node?["key1"]?.GetValue<string>());
        Assert.Equal("value2", node?["key2"]?.GetValue<string>());
    }

    [Fact]
    public void YamlToJson_ConvertsNestedMappings()
    {
        var yaml = """
            parent:
              child1: value1
              child2:
                grandchild: value2
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.Equal("value1", node?["parent"]?["child1"]?.GetValue<string>());
        Assert.Equal("value2", node?["parent"]?["child2"]?["grandchild"]?.GetValue<string>());
    }

    [Fact]
    public void YamlToJson_ConvertsBooleans()
    {
        var yaml = """
            enabled: true
            disabled: false
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.True(node?["enabled"]?.GetValue<bool>());
        Assert.False(node?["disabled"]?.GetValue<bool>());
    }

    [Fact]
    public void YamlToJson_ConvertsNumbers()
    {
        var yaml = """
            integer: 42
            float: 3.14
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.Equal(42, node?["integer"]?.GetValue<long>());
        Assert.Equal(3.14, node?["float"]?.GetValue<double>() ?? 0.0, precision: 2);
    }

    [Fact]
    public void YamlToJson_ConvertsNullValues()
    {
        var yaml = """
            explicit_null: null
            tilde_null: ~
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.Null(node?["explicit_null"]);
        Assert.Null(node?["tilde_null"]);
    }

    [Fact]
    public void YamlToJson_PreservesQuotedStrings()
    {
        var yaml = """
            version: "13.2.0"
            truthy: "true"
            number: "42"
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.Equal("13.2.0", node?["version"]?.GetValue<string>());
        Assert.Equal("true", node?["truthy"]?.GetValue<string>());
        Assert.Equal("42", node?["number"]?.GetValue<string>());
    }

    [Fact]
    public void YamlToJson_ConvertsSequences()
    {
        var yaml = """
            items:
              - one
              - two
              - three
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        var arr = node?["items"]?.AsArray();
        Assert.NotNull(arr);
        Assert.Equal(3, arr.Count);
        Assert.Equal("one", arr[0]?.GetValue<string>());
    }

    [Fact]
    public void YamlToJson_HandlesComments()
    {
        var yaml = """
            # Top-level comment
            key: value  # inline comment
            """;

        var json = YamlJsonConverter.YamlToJson(yaml);
        var node = JsonNode.Parse(json);

        Assert.Equal("value", node?["key"]?.GetValue<string>());
    }

    [Fact]
    public void YamlToJson_HandlesEmptyMapping()
    {
        var json = YamlJsonConverter.YamlToJson("{}");
        var node = JsonNode.Parse(json);

        Assert.NotNull(node);
        Assert.Empty(node.AsObject());
    }

    [Fact]
    public void JsonToYaml_ConvertsSimpleObject()
    {
        var json = """{"key1":"value1","key2":"value2"}""";

        var yaml = YamlJsonConverter.JsonToYaml(json);

        Assert.Contains("key1: value1", yaml);
        Assert.Contains("key2: value2", yaml);
        Assert.DoesNotContain("{", yaml);
    }

    [Fact]
    public void JsonToYaml_ConvertsNestedObjects()
    {
        var json = """{"parent":{"child":"value"}}""";

        var yaml = YamlJsonConverter.JsonToYaml(json);

        Assert.Contains("parent:", yaml);
        Assert.Contains("child: value", yaml);
    }

    [Fact]
    public void JsonToYaml_ConvertsBooleans()
    {
        var json = """{"enabled":true,"disabled":false}""";

        var yaml = YamlJsonConverter.JsonToYaml(json);

        Assert.Contains("enabled: true", yaml);
        Assert.Contains("disabled: false", yaml);
    }

    [Fact]
    public void JsonToYaml_RoundTrips()
    {
        var originalYaml = """
            appHost:
              path: App.csproj
            sdk:
              version: "13.2.0"
            channel: daily
            features:
              enabled: true
            """;

        var json = YamlJsonConverter.YamlToJson(originalYaml);
        var roundTrippedYaml = YamlJsonConverter.JsonToYaml(json);
        var roundTrippedJson = YamlJsonConverter.YamlToJson(roundTrippedYaml);

        var original = JsonNode.Parse(json);
        var roundTripped = JsonNode.Parse(roundTrippedJson);

        Assert.Equal(original?["appHost"]?["path"]?.GetValue<string>(), roundTripped?["appHost"]?["path"]?.GetValue<string>());
        Assert.Equal(original?["sdk"]?["version"]?.GetValue<string>(), roundTripped?["sdk"]?["version"]?.GetValue<string>());
        Assert.Equal(original?["channel"]?.GetValue<string>(), roundTripped?["channel"]?.GetValue<string>());
        Assert.Equal(original?["features"]?["enabled"]?.GetValue<bool>(), roundTripped?["features"]?["enabled"]?.GetValue<bool>());
    }
}
