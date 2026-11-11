// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Tray;

/// <summary>
/// An explicitly selected registration location with optimistic external-edit protection.
/// </summary>
internal interface ITrayStartupRegistrationStore
{
    string? Read();
    void Write(string? expected, string? value);
}

/// <summary>
/// Derives startup state from an owned registration, never from a preference flag.
/// </summary>
internal abstract class RegisteredTrayStartupSettings(ITrayStartupRegistrationStore store) : ITrayStartupSettings
{
    private const string PolicyDetail = "Applies at the next sign-in only. Moving or removing the CLI installation breaks startup. Operating-system startup policy may override this registration.";
    private bool _read;
    private string? _observed;

    protected abstract string? UnavailableReason { get; }
    protected abstract bool IsOwned(string registration);
    protected abstract string CreateRegistration();
    protected virtual void PrepareRegistration() { }

    public TrayStartupState Read()
    {
        var current = ReadOwned();
        _observed = current;
        _read = true;
        return State(current);
    }

    public TrayStartupState SetEnabled(bool enabled)
    {
        if (!_read)
        {
            Read();
        }
        var current = ReadOwned();
        if (!string.Equals(current, _observed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The startup registration changed outside Aspire. Reopen Settings before changing it.");
        }
        string? desired = null;
        if (enabled)
        {
            if (UnavailableReason is { } reason)
            {
                throw new InvalidOperationException(reason);
            }
            desired = CreateRegistration();
            try
            {
                PrepareRegistration();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Aspire could not install its sign-in startup helper. The startup registration was not changed.", ex);
            }
        }
        if (!string.Equals(current, desired, StringComparison.Ordinal))
        {
            try
            {
                store.Write(current, desired);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Aspire could not update the sign-in startup registration.", ex);
            }
        }
        _observed = desired;
        return State(desired);
    }

    private string? ReadOwned()
    {
        string? current;
        try
        {
            current = store.Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Aspire could not read the sign-in startup registration.", ex);
        }
        if (current is not null && !IsOwned(current))
        {
            throw new InvalidOperationException("An unrelated or modified registration occupies Aspire's startup location. It was left unchanged.");
        }
        return current;
    }

    private TrayStartupState State(string? registration)
    {
        var reason = UnavailableReason;
        return new(registration is not null, reason is null,
            reason is null ? PolicyDetail : $"{reason} Existing registration can still be disabled. {PolicyDetail}");
    }
}

/// <summary>
/// Atomically updates a single owned file without changing shared directory permissions.
/// </summary>
internal sealed class FileTrayStartupRegistrationStore(string path) : ITrayStartupRegistrationStore
{
    public string? Read()
    {
        RejectLinks(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64 * 1024)
            {
                throw new InvalidOperationException("The startup registration is unexpectedly large and was left unchanged.");
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Write(string? expected, string? value)
    {
        if (!string.Equals(Read(), expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The startup registration changed outside Aspire. It was left unchanged.");
        }
        if (value is null)
        {
            if (expected is not null)
            {
                File.Delete(path);
            }
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("An absolute startup registration directory is required.");
        // LaunchAgents is shared by other applications. Only our file receives mode 0600;
        // never chmod the user's existing LaunchAgents directory.
        Directory.CreateDirectory(directory);
        RejectLinks(path);
        var temporary = Path.Combine(directory, $".aspire-startup-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(Encoding.UTF8.GetBytes(value));
                stream.Flush(flushToDisk: true);
            }
            if (!string.Equals(Read(), expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The startup registration changed outside Aspire. It was left unchanged.");
            }
            File.Move(temporary, path, overwrite: expected is not null);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void RejectLinks(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("An absolute startup registration path is required.");
        }
        foreach (FileSystemInfo entry in new FileSystemInfo[] { new FileInfo(path), new DirectoryInfo(Path.GetDirectoryName(path)!) })
        {
            if (entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidOperationException("The startup registration must not use symbolic links or reparse points.");
            }
        }
    }
}
