// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/6bb4d76a63d24854efe0fa0bd96f5ab6f699ed3a/core/Azure.Mcp.Core/src/Services/Telemetry
// Keep in sync with updates there.

internal abstract class UnixMachineInformationProvider(ILogger<UnixMachineInformationProvider> logger)
    : MachineInformationProviderBase(logger)
{
    private readonly ILogger<UnixMachineInformationProvider> _logger = logger;

    /// <summary>
    /// Gets the root folder to cache information to.
    /// </summary>
    /// <exception cref="InvalidOperationException">If there is no folder to persist data in.</exception>
    public abstract string GetStoragePath();

    public override async Task<string?> GetOrCreateDeviceId()
    {
        string cachePath;
        try
        {
            cachePath = Path.Combine(GetStoragePath(), MicrosoftDirectory, DeveloperToolsDirectory);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Unable to find folder to cache device id to.");
            return null;
        }

        var existingValue = await ReadValueFromDisk(cachePath, DeviceId);
        if (existingValue != null)
        {
            return existingValue;
        }

        var deviceId = GenerateDeviceId();
        if (await WriteValueToDisk(cachePath, DeviceId, deviceId))
        {
            return deviceId;
        }
        else
        {
            _logger.LogWarning("Unable to persist deviceId.");
            return null;
        }
    }

    /// <summary>
    /// Try and write the value to disk. If <paramref name="value"/> is null or empty, this method will return false.
    /// </summary>
    /// <param name="directoryPath">Directory path to write the file.</param>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="value">The value to write in the file.</param>
    /// <returns>True, if the value was successfully written.</returns>
    /// 
    public async virtual Task<bool> WriteValueToDisk(string directoryPath, string fileName, string? value)
    {
        // If the value is not set, return immediately.
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directoryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to create directory. {Directory}", directoryPath);
            return false;
        }

        var fullPath = Path.Combine(directoryPath, fileName);

        try
        {
            File.Delete(fullPath);

            await File.WriteAllTextAsync(fullPath, value, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to write {Value} to disk. {FullPath}", value, fullPath);
            return false;
        }
    }

    /// <summary>
    /// Try and read the value from disk.
    /// </summary>
    /// <param name="directoryPath">The directory path to read from.</param>
    /// <param name="fileName">The file name to read.</param>
    /// <returns>Returns the value if it could be read from disk. Otherwise, null.</returns>
    public async virtual Task<string?> ReadValueFromDisk(string directoryPath, string fileName)
    {
        var path = Path.Combine(directoryPath, fileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var contents = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return contents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to read value from {FullPath}", path);
            return null;
        }
    }
}
