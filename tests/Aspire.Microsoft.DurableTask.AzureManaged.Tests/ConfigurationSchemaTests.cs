// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace Aspire.Microsoft.DurableTask.AzureManaged.Tests;

public class ConfigurationSchemaTests
{
    private static readonly string s_schemaPath = Path.Combine(
        AppContext.BaseDirectory, "ConfigurationSchema.json");

    [Fact]
    public void SchemaFileExists()
    {
        Assert.True(File.Exists(s_schemaPath), $"Schema file not found at {s_schemaPath}");
    }

    [Fact]
    public void ValidJsonConfigPassesValidation()
    {
        var schema = JsonSchema.FromFile(s_schemaPath);

        var validConfig = JsonNode.Parse("""
            {
              "Aspire": {
                "Microsoft": {
                  "DurableTask": {
                    "AzureManaged": {
                      "ConnectionString": "Endpoint=http://localhost:8080;Authentication=None;TaskHub=MyHub",
                      "DisableHealthChecks": false,
                      "DisableTracing": false
                    }
                  }
                }
              }
            }
            """);

        var results = schema.Evaluate(validConfig);
        Assert.True(results.IsValid);
    }

    [Theory]
    [InlineData("""{"Aspire": { "Microsoft": { "DurableTask": { "AzureManaged": { "DisableHealthChecks": "notabool"}}}}}""", "Value is \"string\" but should be \"boolean\"")]
    [InlineData("""{"Aspire": { "Microsoft": { "DurableTask": { "AzureManaged": { "DisableTracing": "notabool"}}}}}""", "Value is \"string\" but should be \"boolean\"")]
    public void InvalidJsonConfigFailsValidation(string json, string expectedError)
    {
        var schema = JsonSchema.FromFile(s_schemaPath);

        var config = JsonNode.Parse(json);
        var results = schema.Evaluate(config, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var detail = results.Details.FirstOrDefault(x => x.HasErrors);

        Assert.NotNull(detail);
        Assert.Equal(expectedError, detail.Errors!.First().Value);
    }
}
