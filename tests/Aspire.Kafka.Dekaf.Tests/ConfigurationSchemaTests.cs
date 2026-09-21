// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Json.Schema;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

public class ConfigurationSchemaTests
{
    [Theory]
    [InlineData("Producer", true)]
    [InlineData("Producer", false)]
    [InlineData("Consumer", true)]
    [InlineData("Consumer", false)]
    [InlineData("AdminClient", true)]
    [InlineData("AdminClient", false)]
    [InlineData("SchemaRegistry", true)]
    [InlineData("SchemaRegistry", false)]
    public void SchemaValidatesAspireSettings(string role, bool valid)
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "ConfigurationSchema.json"),
            new BuildOptions { Dialect = Dialect.Draft07, SchemaRegistry = new SchemaRegistry() });
        using var configuration = JsonDocument.Parse($$"""
            {
              "Aspire": {
                "Kafka": {
                  "Dekaf": {
                    "{{role}}": {
                      "ConnectionString": "localhost:9092",
                      "DisableHealthChecks": {{(valid ? "false" : "123")}},
                      "Config": { "ClientId": "schema-test" },
                      "HealthCheck": { "Timeout": "00:00:02" }
                    }
                  }
                }
              }
            }
            """);

        Assert.Equal(valid, schema.Evaluate(configuration.RootElement).IsValid);
    }
}
