// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using StreamJsonRpc;

namespace Aspire.Cli.Backchannel;

internal interface IExtensionRpcTarget
{
    Func<string, ValidationResult>? ValidationFunction { get; set; }

    [JsonRpcMethod("getCliVersion")]
    Task<string> GetCliVersionAsync();

    [JsonRpcMethod("validatePromptInputString")]
    Task<ValidationResult?> ValidatePromptInputStringAsync(string input);

    [JsonRpcMethod("stopCli")]
    Task StopCliAsync();

    [JsonRpcMethod("getDebugSessionId")]
    Task<string?> GetDebugSessionIdAsync();

    [JsonRpcMethod("getCliCapabilities")]
    Task<string[]> GetCliCapabilitiesAsync();
}

internal class ExtensionRpcTarget(IConfiguration configuration) : IExtensionRpcTarget
{
    public Func<string, ValidationResult>? ValidationFunction { get; set; }

    public Task<string> GetCliVersionAsync()
    {
        return Task.FromResult(VersionHelper.GetDefaultTemplateVersion());
    }

    public Task<ValidationResult?> ValidatePromptInputStringAsync(string input)
    {
        return Task.FromResult(ValidationFunction?.Invoke(input));
    }

    public Task StopCliAsync()
    {
        Environment.Exit(ExitCodeConstants.Success);
        return Task.CompletedTask;
    }

    public Task<string?> GetDebugSessionIdAsync()
    {
        return Task.FromResult(configuration[KnownConfigNames.ExtensionDebugSessionId]);
    }

    public Task<string[]> GetCliCapabilitiesAsync()
    {
        return Task.FromResult(KnownCapabilities.GetAdvertisedCapabilities());
    }
}
