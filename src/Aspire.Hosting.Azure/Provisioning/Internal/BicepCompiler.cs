// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IBicepCompiler"/>.
/// </summary>
internal sealed class BicepCliCompiler : IBicepCompiler
{
    private readonly ILogger<BicepCliCompiler> _logger;
    private readonly SemaphoreSlim _compileLock = new(1, 1);

    public BicepCliCompiler(ILogger<BicepCliCompiler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default)
    {
        await _compileLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Try bicep command first for better performance
            var bicepPath = PathLookupHelper.FindFullPathFromPath("bicep");
            string commandPath;
            string arguments;

            if (bicepPath is not null)
            {
                commandPath = bicepPath;
                arguments = $"build \"{bicepFilePath}\" --stdout";
            }
            else
            {
                // Fall back to az bicep if bicep command is not available
                var azPath = PathLookupHelper.FindFullPathFromPath("az");
                if (azPath is null)
                {
                    throw new AzureCliNotOnPathException();
                }
                commandPath = azPath;
                arguments = $"bicep build --file \"{bicepFilePath}\" --stdout";
            }

            var armTemplateContents = new StringBuilder();
            var errorContents = new StringBuilder();
            var templateSpec = new ProcessSpec(commandPath)
            {
                Arguments = arguments,
                OnOutputData = data =>
                {
                    _logger.LogDebug("{CommandPath} (stdout): {Output}", commandPath, data);
                    armTemplateContents.AppendLine(data);
                },
                OnErrorData = data =>
                {
                    _logger.LogDebug("{CommandPath} (stderr): {Error}", commandPath, data);
                    errorContents.AppendLine(data);
                },
            };

            _logger.LogDebug("Running {CommandPath} with arguments: {Arguments}", commandPath, arguments);

            int exitCode;
            try
            {
                exitCode = await ExecuteCommand(templateSpec).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                var errorMessage = errorContents.ToString().Trim();
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(errorMessage)
                        ? $"Failed to compile bicep file: {bicepFilePath}"
                        : $"Failed to compile bicep file: {bicepFilePath}{Environment.NewLine}{errorMessage}",
                    ex);
            }

            if (exitCode != 0)
            {
                _logger.LogError("Bicep compilation for {BicepFilePath} failed with exit code {ExitCode}.", bicepFilePath, exitCode);
                var errorMessage = errorContents.ToString().Trim();
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(errorMessage)
                        ? $"Failed to compile bicep file: {bicepFilePath}"
                        : $"Failed to compile bicep file: {bicepFilePath}{Environment.NewLine}{errorMessage}");
            }

            _logger.LogDebug("Bicep compilation for {BicepFilePath} succeeded.", bicepFilePath);

            return armTemplateContents.ToString();
        }
        finally
        {
            _compileLock.Release();
        }
    }

    private static async Task<int> ExecuteCommand(ProcessSpec processSpec)
    {
        var sw = Stopwatch.StartNew();
        var (task, disposable) = ProcessUtil.Run(processSpec);

        try
        {
            var result = await task.ConfigureAwait(false);
            sw.Stop();

            return result.ExitCode;
        }
        finally
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
