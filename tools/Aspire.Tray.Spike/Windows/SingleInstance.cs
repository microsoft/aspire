// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Security.Principal;

namespace Aspire.Tray.Spike;

[SupportedOSPlatform("windows")]
internal sealed class SingleInstance(Mutex mutex) : IDisposable
{
    public static SingleInstance? Acquire()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is null)
        {
            throw new InvalidOperationException("Windows did not provide the current user's SID.");
        }

        // Global spans terminal sessions; the SID separates users. This name deliberately
        // differs from any production singleton. Default Windows object ACLs still apply.
        var mutex = new Mutex(initiallyOwned: false, $@"Global\Aspire.Tray.Spike.User.{sid}");
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

            return new SingleInstance(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
