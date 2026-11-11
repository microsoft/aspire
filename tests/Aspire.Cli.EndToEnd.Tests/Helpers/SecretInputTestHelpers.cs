// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class SecretInputTestHelpers
{
    internal static async Task VerifyInputSourcesAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        string appHostOptions)
    {
        const string environmentMarker = "environment-secret-not-for-diagnostics";
        const string environmentValue = environmentMarker + "\n  $literal [value] \"quoted\"  ";
        const string promptedValue = "prompted-secret-not-for-diagnostics";
        const string cancelledValue = "cancelled-secret-not-for-diagnostics";
        const string key = "Parameters:input-secret";
        var setCommand = $"aspire secret set {key}{appHostOptions}";
        var valuePath = Path.Combine(workspace.Path, "secret-value.txt");
        var sourcePath = Path.Combine(workspace.Path, "secret-source.txt");
        var patternsPath = Path.Combine(workspace.Path, "secret-log-patterns.txt");
        var containerValuePath = AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(valuePath, workspace));
        var containerSourcePath = AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(sourcePath, workspace));
        var containerPatternsPath = AspireCliShellCommandHelpers.QuoteBashArg(CliE2ETestHelpers.ToContainerPath(patternsPath, workspace));
        var getCommand = $"aspire secret get {key}{appHostOptions} > {containerValuePath}";

        // Keep the value out of shell commands and terminal history as well as CLI arguments.
        await File.WriteAllTextAsync(sourcePath, environmentValue);
        await File.WriteAllLinesAsync(patternsPath, [environmentMarker, promptedValue, cancelledValue]);
        await auto.RunCommandAsync($"export ASPIRE_E2E_SECRET_VALUE=\"$(cat {containerSourcePath})\"; unset ASPIRE_E2E_MISSING_SECRET", counter);
        await auto.ClearScreenAsync(counter);
        await auto.RunCommandAsync($"{setCommand} --envvar ASPIRE_E2E_SECRET_VALUE --log-level Trace", counter);
        Assert.False(auto.CreateSnapshot().ContainsText(environmentMarker));
        await AssertStoredValueAsync(environmentValue);

        await AssertCommandFailsAsync(
            $"{setCommand} positional-value --envvar ASPIRE_E2E_SECRET_VALUE",
            "Specify either a positional secret value or --envvar, not both.");
        await AssertCommandFailsAsync(
            $"{setCommand} --envvar ASPIRE_E2E_MISSING_SECRET",
            "Environment variable 'ASPIRE_E2E_MISSING_SECRET' is not set.");
        await AssertCommandFailsAsync(
            $"{setCommand} --non-interactive",
            "Specify a positional secret value or --envvar");
        await AssertStoredValueAsync(environmentValue);

        await auto.RunCommandAsync("export ASPIRE_E2E_SECRET_VALUE=''", counter);
        await auto.RunCommandAsync($"{setCommand} --envvar ASPIRE_E2E_SECRET_VALUE --non-interactive", counter);
        await AssertStoredValueAsync("");
        await auto.RunCommandAsync($"{setCommand} '' --non-interactive", counter);
        await AssertStoredValueAsync("");

        await StartSecretPromptAsync();
        await auto.TypeAsync(promptedValue);
        await WaitForMaskedValueAsync(promptedValue);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));
        await AssertStoredValueAsync(promptedValue);

        await StartSecretPromptAsync();
        await auto.TypeAsync(cancelledValue);
        await WaitForMaskedValueAsync(cancelledValue);
        var cancelledSequence = counter.Value;
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(60));
        Assert.True(auto.CreateSnapshot().ContainsText($"[{cancelledSequence} ERR:{CliExitCodes.Cancelled}] $"));
        await AssertStoredValueAsync(promptedValue);

        await StartSecretPromptAsync();
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));
        await AssertStoredValueAsync("");

        // grep returns 1 only when no pattern matches; a missing log directory must also fail.
        await auto.RunCommandAsync(
            $"grep -RFq -f {containerPatternsPath} \"$HOME/.aspire/logs\"; test $? -eq 1",
            counter);

        async Task AssertStoredValueAsync(string expected)
        {
            await auto.RunCommandAsync(getCommand, counter, TimeSpan.FromSeconds(60));
            Assert.Equal(expected + "\n", await File.ReadAllTextAsync(valuePath));
        }

        async Task AssertCommandFailsAsync(string command, string expectedError)
        {
            await auto.ClearScreenAsync(counter);
            var sequence = counter.Value;
            await auto.TypeAsync(command);
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync(expectedError, timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForAnyPromptAsync(counter, TimeSpan.FromSeconds(60));
            Assert.True(auto.CreateSnapshot().ContainsText($"[{sequence} ERR:{CliExitCodes.InvalidCommand}] $"));
        }

        async Task StartSecretPromptAsync()
        {
            await auto.ClearScreenAsync(counter);
            await auto.TypeAsync($"{setCommand} --log-level Trace");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Enter the secret value:", timeout: TimeSpan.FromSeconds(60));
        }

        async Task WaitForMaskedValueAsync(string value)
        {
            await auto.WaitUntilAsync(
                snapshot => snapshot.ContainsText(new string('*', value.Length)),
                timeout: TimeSpan.FromSeconds(30),
                description: "waiting for the secret input to be masked");
            Assert.False(auto.CreateSnapshot().ContainsText(value));
        }
    }
}
