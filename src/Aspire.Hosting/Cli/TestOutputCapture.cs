// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Cli;

/// <summary>
/// Persists separate process-owned output streams without dashboard rendering or bounded replay.
/// </summary>
internal static class TestOutputCapture
{
    internal static async Task CaptureAsync(IResource resource, ResourceLoggerService logs,
        ResourceTestingContext context, CancellationToken cancellationToken)
    {
        if (resource is not ExecutableResource || resource.GetReplicaCount() != 1 ||
            resource.TryGetLastAnnotation<TerminalAnnotation>(out _))
        {
            throw new NotSupportedException("Raw test output capture supports one native, non-terminal executable.");
        }
        var names = resource.GetResolvedResourceNames();
        if (names.Length != 1)
        {
            throw new NotSupportedException("Raw test output capture does not support replicas.");
        }
        for (var directory = new DirectoryInfo(context.OutputPath); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The output directory must not contain symbolic links or reparse points.");
            }
            if (directory.FullName == context.RunContext.OutputPath)
            {
                break;
            }
        }

        // Open both destinations before draining either stream. CreateNew prevents reusing a stale
        // report or following an existing file symlink. Mark completion only after both streams drain.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdout = CreateFile(context.OutputPath, "stdout.log");
        await using (stdout.ConfigureAwait(false))
        {
            var stderr = CreateFile(context.OutputPath, "stderr.log");
            await using (stderr.ConfigureAwait(false))
            {
                await Task.WhenAll(CopyAsync(stdout, false), CopyAsync(stderr, true)).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var complete = CreateFile(context.OutputPath, "output.complete");
        await complete.DisposeAsync().ConfigureAwait(false);

        async Task CopyAsync(Stream destination, bool standardError)
        {
            try
            {
                var source = await logs.GetRawOutputAsync(names[0], standardError, stop.Token).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destination, stop.Token).ConfigureAwait(false);
                    await destination.FlushAsync(stop.Token).ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                // If one stream fails, cancel its sibling rather than waiting indefinitely for EOF.
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static FileStream CreateFile(string directory, string name)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(Path.Combine(directory, name), options);
    }
}
