// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Security.Principal;

namespace Aspire.Tray;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSingleInstance(Mutex mutex) : IDisposable
{
    public static string DirectoryPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Path.IsPathFullyQualified(localAppData))
            {
                throw new InvalidOperationException("Windows did not provide a per-user local application data directory.");
            }

            return Path.Combine(localAppData, "Aspire", "Tray");
        }
    }

    public static string ActivationPipeName => $"Aspire.Tray.User.{GetUserSid()}.control-v1";

    public static WindowsSingleInstance? TryAcquire()
        => TryAcquire($"Aspire.Tray.User.{GetUserSid()}");

    internal static WindowsSingleInstance? TryAcquire(string name)
    {
        // Enforce the current-user ACL as well as separating names by SID. A global
        // mutex spans terminal sessions; ownership must be released on the acquiring thread.
        var mutex = new Mutex(initiallyOwned: false, name, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        });
        try
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // WaitOne granted ownership after the previous tray process exited uncleanly.
            }

            return new WindowsSingleInstance(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static string GetUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is null)
        {
            throw new InvalidOperationException("Windows did not provide the current user's SID.");
        }

        return sid;
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
