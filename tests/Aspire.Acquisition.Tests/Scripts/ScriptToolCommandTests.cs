// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class ScriptToolCommandTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task BashScript_OutputPreservesOrderAcrossStdoutAndStderr()
    {
        const int pairs = 500;
        using var env = new TestEnvironment();
        var scriptPath = Path.Combine(env.TempDirectory, "interleave.sh");
        await File.WriteAllTextAsync(scriptPath, $$"""
            #!/bin/bash
            for i in $(seq 1 {{pairs}}); do
                printf 'stdout %d\n' "$i"
                printf 'stderr %d\n' "$i" >&2
            done
            """.ReplaceLineEndings("\n"));

        using var cmd = new ScriptToolCommand(scriptPath, env, testOutput);
        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var expected = Enumerable.Range(1, pairs).SelectMany(i => new[] { $"stdout {i}", $"stderr {i}" });
        Assert.Equal(expected, result.Output.Split(Environment.NewLine));
    }
}
