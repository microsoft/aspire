// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Marks a test that requires Linux Docker containers. On Windows, these tests
/// are automatically skipped because the E2E Docker containers are Linux-based.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RequiresLinuxDockerFactAttribute : FactAttribute
{
    public RequiresLinuxDockerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "This test requires Linux Docker containers and cannot run on Windows.";
        }
    }
}
