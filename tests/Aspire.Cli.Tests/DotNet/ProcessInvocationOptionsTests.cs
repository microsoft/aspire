// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.DotNet;

public sealed class ProcessInvocationOptionsTests
{
    [Fact]
    public void Clone_CopiesEveryProperty()
    {
        // Reflection-driven on purpose: adding a property to ProcessInvocationOptions without also
        // forwarding it in Clone() must fail this test rather than silently drop the value on the copy —
        // the exact "silently break" hazard Clone() exists to guard against.
        var original = new ProcessInvocationOptions
        {
            StandardOutputCallback = _ => { },
            StandardErrorCallback = _ => { },
            NoLaunchProfile = true,
            StartDebugSession = true,
            Debug = true,
            SuppressLogging = true,
            // Non-default: this property initializes to true, so false is the "changed" value.
            KillEntireProcessTreeOnCancel = false,
            IsolateConsole = true,
            KillOnParentExit = true,
            Detached = true,
            StdioMode = ProcessStdioMode.Suppress,
            EnvironmentVariableFilter = _ => false,
            GracefulShutdownSignaler = new RecordingGracefulSignaler(),
            ShutdownService = new TestGracefulShutdownWindow(),
        };

        // Compare against a pristine instance so we assert every property was given a value that
        // actually differs from its default; otherwise the copy check below could pass vacuously.
        var defaults = new ProcessInvocationOptions();
        foreach (var property in GetSettableProperties())
        {
            Assert.False(
                Equals(property.GetValue(original), property.GetValue(defaults)),
                $"Property '{property.Name}' was not set to a non-default value in this test; add one so Clone() coverage stays meaningful.");
        }

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        foreach (var property in GetSettableProperties())
        {
            Assert.Equal(property.GetValue(original), property.GetValue(clone));
        }
    }

    private static IEnumerable<PropertyInfo> GetSettableProperties() =>
        typeof(ProcessInvocationOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true });
}
