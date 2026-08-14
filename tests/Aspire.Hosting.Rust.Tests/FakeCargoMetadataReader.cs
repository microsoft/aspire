// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Rust.Tests;

/// <summary>
/// Answers <c>cargo metadata</c> queries from a canned document instead of shelling out to cargo, so tests
/// can exercise publishing and debugging on machines with no Rust toolchain installed.
/// </summary>
/// <param name="metadataJson">The document to answer with.</param>
internal sealed class FakeCargoMetadataReader(string metadataJson) : ICargoMetadataReader
{
    /// <summary>
    /// The environment the reader was last asked to query with.
    /// </summary>
    public IReadOnlyDictionary<string, string> LastEnvironment { get; private set; } = new Dictionary<string, string>();

    /// <summary>
    /// The directory the reader was last asked to query in.
    /// </summary>
    public string? LastWorkingDirectory { get; private set; }

    /// <summary>
    /// How many times the reader has been asked for metadata.
    /// </summary>
    /// <remarks>
    /// Each launch request reads current metadata once, so tests use this count to catch duplicate reads
    /// inside one request without hiding manifest or environment changes from later executable creations.
    /// </remarks>
    public int ReadCount => _readCount;

    /// <summary>
    /// Optional hook invoked before the canned document is returned, so a test can stand in for a slow cargo.
    /// </summary>
    public Func<CancellationToken, Task>? OnRead { get; set; }

    /// <summary>
    /// Optional factory for returning different metadata documents on successive reads.
    /// </summary>
    public Func<int, string>? MetadataJsonFactory { get; set; }

    private int _readCount;

    public async Task<CargoMetadata> ReadAsync(string workingDirectory, string? manifestPath, string resourceName, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var readCount = Interlocked.Increment(ref _readCount);
        LastEnvironment = environment;
        LastWorkingDirectory = workingDirectory;

        if (OnRead is { } onRead)
        {
            await onRead(cancellationToken).ConfigureAwait(false);
        }

        var rebased = MetadataJsonFactory?.Invoke(readCount) ?? metadataJson;

        // Real cargo honours CARGO_TARGET_DIR when reporting target_directory, and the debug executable path
        // is derived from it, so the fake reflects it too.
        if (environment.TryGetValue("CARGO_TARGET_DIR", out var targetDirectory) && targetDirectory.Length > 0)
        {
            rebased = rebased.Replace(
                "\"target_directory\": \"/app/target\"",
                $"\"target_directory\": {JsonSerializer.Serialize(Path.GetFullPath(targetDirectory, workingDirectory))}",
                StringComparison.Ordinal);
        }

        return CargoMetadata.Parse(rebased);
    }
}
