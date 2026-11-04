// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using System.Security.Principal;

namespace Aspire.Tray.Tests;

public class WindowsSingleInstanceTests
{
    public static bool SupportsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "The state directory and SID are Windows-specific.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void StateAndActivationArePerUserAndVersionIndependent()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aspire", "Tray"),
            WindowsSingleInstance.DirectoryPath);
        Assert.Equal($"Aspire.Tray.User.{identity.User!.Value}.control-v1", WindowsSingleInstance.ActivationPipeName);
    }

    [Fact(Skip = "The mutex uses Windows current-user ACLs.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void MutexOwnershipIsExclusiveUntilReleasedOnItsOwningThread()
    {
        var name = $"Aspire.Tray.Tests.{Guid.NewGuid():N}";
        using (var owner = WindowsSingleInstance.TryAcquire(name))
        {
            Assert.NotNull(owner);
            Exception? failure = null;
            var contender = new Thread(() =>
            {
                try
                {
                    using var competing = WindowsSingleInstance.TryAcquire(name);
                    Assert.Null(competing);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
        }

        using var nextOwner = WindowsSingleInstance.TryAcquire(name);
        Assert.NotNull(nextOwner);
    }
}
