// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// A cross-process file-based lock that is safe to use with async/await.
/// </summary>
/// <remarks>
/// <para>
/// This is used instead of <see cref="System.Threading.Mutex"/> because:
/// </para>
/// <list type="bullet">
/// <item>Named mutexes with <c>Global\</c> prefix behave differently on Linux vs Windows.</item>
/// <item><see cref="System.Threading.Mutex"/> has thread affinity — <c>ReleaseMutex</c> must be
/// called from the same thread that called <c>WaitOne</c>, which is incompatible with
/// async/await where continuations may run on a different thread.</item>
/// </list>
/// <para>
/// A <see cref="FileStream"/> opened with <see cref="FileShare.None"/> provides exclusive
/// access that works cross-platform and has no thread affinity. The lock is released when
/// the stream is disposed.
/// </para>
/// <para>
/// Based on the locking pattern from NuGet.Common.ConcurrencyUtilities.
/// </para>
/// </remarks>
internal sealed class FileLock : IDisposable
{
    // Short delay keeps latency low under contention without busy-spinning.
    // Matches the delay used by NuGet.Common.ConcurrencyUtilities.
    private static readonly TimeSpan s_defaultRetryDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(5);

    private readonly FileStream _stream;

    private FileLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Acquires an exclusive file lock without waiting.
    /// </summary>
    public static FileLock Acquire(string lockPath)
    {
        CreateLockDirectory(lockPath);

        return new FileLock(CreateLockStream(lockPath));
    }

    /// <summary>
    /// Acquires an exclusive file lock synchronously, retrying on contention.
    /// </summary>
    /// <param name="lockPath">The full path of the lock file.</param>
    /// <param name="timeout">Maximum time to wait for the lock.</param>
    /// <returns>A <see cref="FileLock"/> that releases the lock when disposed.</returns>
    /// <exception cref="TimeoutException">Thrown if the lock cannot be acquired within the timeout period.</exception>
    public static FileLock Acquire(string lockPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        CreateLockDirectory(lockPath);

        while (true)
        {
            try
            {
                return new FileLock(CreateLockStream(lockPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Failed to acquire file lock '{lockPath}' within {timeout.TotalSeconds:F0} seconds.", exception);
                }
            }

            Thread.Sleep(s_defaultRetryDelay);
        }
    }

    /// <summary>
    /// Attempts to acquire an exclusive file lock without waiting.
    /// </summary>
    public static FileLock? TryAcquire(string lockPath)
    {
        try
        {
            return Acquire(lockPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Acquires an exclusive file lock, retrying on contention.
    /// Uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/> between retries to avoid blocking the thread pool.
    /// </summary>
    /// <param name="lockPath">The full path of the lock file.</param>
    /// <param name="cancellationToken">Token to cancel the wait for the lock.</param>
    /// <param name="timeout">Maximum time to wait for the lock. Defaults to 5 minutes.</param>
    /// <returns>A <see cref="FileLock"/> that releases the lock when disposed.</returns>
    /// <exception cref="TimeoutException">Thrown if the lock cannot be acquired within the timeout period.</exception>
    public static async Task<FileLock> AcquireAsync(string lockPath, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? s_defaultTimeout;
        var deadline = DateTime.UtcNow + effectiveTimeout;

        CreateLockDirectory(lockPath);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileLock(CreateLockStream(lockPath));
            }
            catch (IOException)
            {
                // Sharing violation — another process holds the lock. On Windows the
                // FileStream constructor throws immediately; on Unix it may also throw
                // if the file is exclusively locked. Wait and retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Can occur transiently when the lock file is being deleted
                // (DeleteOnClose) by the process that just released the lock,
                // or if an admin/antivirus has the file temporarily locked.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Failed to acquire file lock '{lockPath}' within {effectiveTimeout.TotalSeconds:F0} seconds.");
            }

            await Task.Delay(s_defaultRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the OS-level file lock and deletes the lock file (<see cref="FileOptions.DeleteOnClose"/>).
    /// </summary>
    public void Dispose()
    {
        _stream.Dispose();
    }

    private static void CreateLockDirectory(string lockPath)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Opens the lock file with exclusive access. Only one process can hold the
    /// handle at a time. <see cref="FileOptions.DeleteOnClose"/> ensures the lock
    /// file is cleaned up automatically when the handle is released.
    /// </summary>
    private static FileStream CreateLockStream(string lockPath)
    {
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }
}