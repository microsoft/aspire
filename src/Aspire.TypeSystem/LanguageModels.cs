// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TypeSystem;

/// <summary>
/// Request for scaffolding a new AppHost project.
/// </summary>
public sealed class ScaffoldRequest
{
    /// <summary>
    /// Gets the target directory path for the project.
    /// </summary>
    public required string TargetPath { get; init; }

    /// <summary>
    /// Gets the project name. If null, derived from directory name.
    /// </summary>
    public string? ProjectName { get; init; }

    /// <summary>
    /// Gets an optional seed for deterministic port generation (for testing).
    /// </summary>
    public int? PortSeed { get; init; }
}

/// <summary>
/// Result of detecting an AppHost in a directory.
/// </summary>
public sealed class DetectionResult
{
    /// <summary>
    /// Gets whether a valid AppHost was detected.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Gets the detected language (e.g., "TypeScript").
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets the path to the detected AppHost file.
    /// </summary>
    public string? AppHostFile { get; init; }

    /// <summary>
    /// Creates a result indicating no AppHost was detected.
    /// </summary>
    public static DetectionResult NotFound => new() { IsValid = false };

    /// <summary>
    /// Creates a result indicating an AppHost was detected.
    /// </summary>
    public static DetectionResult Found(string language, string appHostFile) => new()
    {
        IsValid = true,
        Language = language,
        AppHostFile = appHostFile
    };
}
