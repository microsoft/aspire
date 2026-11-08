// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IBicepCompiler"/>.
/// </summary>
internal sealed class BicepCliCompiler(ILogger<BicepCliCompiler> logger, IProcessRunner processRunner) : IBicepCompiler
{
    public async Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default)
    {
        ProcessCaptureResult result;
        try
        {
            result = await CompileAsync("bicep", ["build", bicepFilePath, "--stdout"], cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception) when (exception.FileName == "bicep")
        {
            try
            {
                result = await CompileAsync("az", ["bicep", "build", "--file", bicepFilePath, "--stdout"], cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException azException) when (azException.FileName == "az")
            {
                throw new AzureCliNotOnPathException();
            }
        }

        if (result.ExitCode != 0)
        {
            logger.LogError("Bicep compilation for {BicepFilePath} failed with exit code {ExitCode}.", bicepFilePath, result.ExitCode);
            var errorMessage = result.StandardError.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(errorMessage)
                ? $"Failed to compile bicep file: {bicepFilePath}"
                : $"Failed to compile bicep file: {bicepFilePath}{Environment.NewLine}{errorMessage}");
        }
        logger.LogDebug("Bicep compilation for {BicepFilePath} succeeded.", bicepFilePath);
        return result.StandardOutput;
    }

    private async Task<ProcessCaptureResult> CompileAsync(string command, string[] arguments, CancellationToken cancellationToken)
    {
        logger.LogDebug("Running {CommandPath} with arguments: {Arguments}", command, arguments);
        var result = await processRunner.CreateExecution(new ProcessCommand(command)
        {
            Arguments = arguments,
            ResolveExecutablePath = true
        }).DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug("{CommandPath} (stdout): {Output}", command, result.StandardOutput);
        logger.LogDebug("{CommandPath} (stderr): {Error}", command, result.StandardError);
        return result;
    }
}
