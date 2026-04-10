// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Marks a test that requires a Linux environment (uses bash commands, Linux tools, etc.).
/// On Windows, these tests are automatically skipped.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RequiresLinuxFactAttribute : FactAttribute
{
    public RequiresLinuxFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "This test requires a Linux environment (uses bash commands and Linux tools).";
        }
    }
}
