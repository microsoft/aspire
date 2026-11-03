// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;

namespace Aspire.Tray;

/// <summary>
/// Installs one immutable, owned GUI bootstrap; it never rewrites an executing image.
/// </summary>
internal sealed class TrayStartupBootstrap(string sourceExecutable, string bootstrapPath)
{
    private const string Owner = "Aspire.Tray.LoginBootstrap.v1:";
    private readonly FileTrayStartupRegistrationStore _ownerStore = new(bootstrapPath + ".owner");

    public string? GetUnavailableReason()
    {
        try
        {
            if (!Path.IsPathFullyQualified(sourceExecutable) || !File.Exists(sourceExecutable)
                || !TrayStartupEntry.IsNativeExecutable(sourceExecutable, windows: true, requireGui: true))
            {
                return "Launch at sign-in requires the native Windows GUI tray executable.";
            }
            ValidateExisting();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return "The existing startup helper could not be verified. Its files were left unchanged.";
        }
    }

    public void EnsureCreated()
    {
        if (GetUnavailableReason() is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
        if (File.Exists(bootstrapPath))
        {
            // login-start is a deliberately stable protocol. Reuse our verified bootstrap
            // across backend upgrades, including when it is executing; the CLI supplies
            // the current backend. Disabling removes Run, not this idle executable.
            return;
        }
        var directory = Path.GetDirectoryName(bootstrapPath)!;
        Directory.CreateDirectory(directory);
        FileTrayStartupRegistrationStore.RejectLinks(bootstrapPath);
        var temporary = Path.Combine(directory, $".aspire-login-{Guid.NewGuid():N}.tmp");
        var previousOwner = _ownerStore.Read();
        try
        {
            File.Copy(sourceExecutable, temporary, overwrite: false);
            var fingerprint = Fingerprint(temporary);
            _ownerStore.Write(previousOwner, fingerprint);
            try
            {
                // No overwrite: a concurrent enable or foreign file cannot be replaced.
                // Byte-for-byte copying preserves the source executable's Authenticode signature.
                File.Move(temporary, bootstrapPath, overwrite: false);
            }
            catch
            {
                _ownerStore.Write(fingerprint, previousOwner);
                throw;
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void ValidateExisting()
    {
        FileTrayStartupRegistrationStore.RejectLinks(bootstrapPath);
        var owner = _ownerStore.Read();
        if (owner is not null && (!owner.StartsWith(Owner, StringComparison.Ordinal)
            || owner.Length != Owner.Length + 16 || owner.AsSpan(Owner.Length).IndexOfAnyExcept("0123456789ABCDEF") >= 0))
        {
            throw new InvalidOperationException("The startup helper ownership record is not recognized.");
        }
        if (File.Exists(bootstrapPath) && !string.Equals(owner, Fingerprint(bootstrapPath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The startup helper was changed outside Aspire.");
        }
    }

    private static string Fingerprint(string path)
    {
        // Non-cryptographic external-edit detection, not an authenticity/signing decision.
        // Trust comes from copying the current NativeAOT GUI; Windows retains its signature.
        var hash = new XxHash3();
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024)
        {
            throw new InvalidOperationException("The startup helper is unexpectedly large.");
        }
        hash.Append(stream);
        return Owner + Convert.ToHexString(hash.GetCurrentHash());
    }
}
