// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class LaunchSettingsSerializerContextTests
{
    [Fact]
    public void CommentsInLaunchSettingsJsonDoesNotThrow()
    {
        const string launchSettingsJson = """
        {
          "$schema": "http://json.schemastore.org/launchsettings.json",
          "profiles": {
            // comment
            "http": {
              "commandName": "Project",
              "dotnetRunMessages": true,
              "launchBrowser": true,
              "applicationUrl": "http://localhost:5261",
              "environmentVariables": {
                "ASPNETCORE_ENVIRONMENT": "Development"
              }
            }
          }
        }
        """;

        // should not throw
        JsonSerializer.Deserialize(launchSettingsJson, LaunchSettingsSerializerContext.Default.LaunchSettings);
    }
}
