// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TypeScriptIntegrationPlaygroundTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("")]
    [InlineData("restore:TsIntegrationSpike")]
    [InlineData("check:TsIntegrationSpike:kafka-integration/tsconfig.json")]
    [InlineData("restore:aspire-kafka")]
    [InlineData("check:aspire-kafka:tsconfig.json")]
    [InlineData("restore:app")]
    [InlineData("check:app:tsconfig.json")]
    [RequiresTools(["bash"])]
    public async Task RestoresAndChecksBothPlaygroundsAndPropagatesFailures(string failingAction)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var repository = workspace.CreateDirectory("repository");
        var tools = workspace.CreateDirectory("tools");
        var log = Path.Combine(workspace.Path, "commands.txt");
        foreach (var path in new[]
        {
            "playground/TsIntegrationSpike",
            "playground/TsKafkaLib/packages/aspire-kafka",
            "playground/TsKafkaLib/app"
        })
        {
            Directory.CreateDirectory(Path.Combine(repository.FullName, path));
        }
        File.WriteAllText(log, "");
        WriteTool(tools.FullName, "aspire", """
            #!/bin/bash
            set -euo pipefail
            action="restore:$(basename "$PWD")"
            printf '%s\n' "$action" >> "$LOG_FILE"
            [ "$action" != "$FAILING_ACTION" ] || exit 9
            mkdir -p .aspire/modules
            printf sdk > .aspire/modules/aspire.mts
            """);
        WriteTool(tools.FullName, "tsgo", """
            #!/bin/bash
            set -euo pipefail
            test -f .aspire/modules/aspire.mts
            test "$1" = "--noEmit"
            test "$2" = "--project"
            action="check:$(basename "$PWD"):$3"
            printf '%s\n' "$action" >> "$LOG_FILE"
            [ "$action" != "$FAILING_ACTION" ] || exit 9
            """);

        var result = await ProcessRunner.RunAsync(output, "bash",
        [
            "-c",
            """
            export PATH="$1:$PATH"
            export LOG_FILE="$2"
            export FAILING_ACTION="$3"
            exec bash "$4" "$5"
            """,
            "test",
            tools.FullName,
            log,
            failingAction,
            Path.Combine(RepoRoot.Path, ".github/workflows/polyglot-validation/test-typescript-integrations.sh"),
            repository.FullName
        ], workspace.Path);

        string[] expected =
        [
            "restore:TsIntegrationSpike",
            "check:TsIntegrationSpike:tsconfig.apphost.json",
            "check:TsIntegrationSpike:kafka-integration/tsconfig.json",
            "check:TsIntegrationSpike:deno-integration/tsconfig.json",
            "restore:aspire-kafka",
            "check:aspire-kafka:tsconfig.json",
            "restore:app",
            "check:app:tsconfig.json"
        ];
        var failureIndex = Array.IndexOf(expected, failingAction);
        Assert.Equal(failureIndex < 0 ? 0 : 9, result.ExitCode);
        Assert.Equal(expected.Take(failureIndex < 0 ? expected.Length : failureIndex + 1), File.ReadAllLines(log));
        Assert.False(Directory.Exists(Path.Combine(repository.FullName, "playground/TsIntegrationSpike/.aspire")));
        Assert.False(Directory.Exists(Path.Combine(repository.FullName, "playground/TsKafkaLib/packages/aspire-kafka/.aspire")));
        Assert.False(Directory.Exists(Path.Combine(repository.FullName, "playground/TsKafkaLib/app/.aspire")));
    }

    private static void WriteTool(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
