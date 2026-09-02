// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Configuration;

namespace Aspire.Managed.NuGet.Commands;

internal static class ConfigPathsCommand
{
    public static Command Create()
    {
        var command = new Command("config-paths", "Lists the effective NuGet configuration hierarchy");
        var workingDirectoryOption = new Option<string>("--working-dir", "-w")
        {
            Description = "Working directory for NuGet.config discovery",
            Required = true
        };
        command.Options.Add(workingDirectoryOption);

        command.SetAction(parseResult =>
        {
            var workingDirectory = parseResult.GetValue(workingDirectoryOption)!;
            Console.WriteLine(JsonSerializer.Serialize(
                GetConfigFilePaths(workingDirectory),
                ConfigPathsJsonContext.Default.StringArray));
            return 0;
        });

        return command;
    }

    internal static string[] GetConfigFilePaths(string workingDirectory)
    {
        var settings = Settings.LoadDefaultSettings(
            workingDirectory,
            configFileName: null,
            new XPlatMachineWideSetting());
        return settings.GetConfigFilePaths().ToArray();
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class ConfigPathsJsonContext : JsonSerializerContext;
