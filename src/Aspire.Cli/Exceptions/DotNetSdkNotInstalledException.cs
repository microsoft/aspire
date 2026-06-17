// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Exceptions;

/// <summary>
/// Exception thrown when the .NET SDK is not installed or doesn't meet the minimum version requirement.
/// </summary>
internal sealed class DotNetSdkNotInstalledException : Exception
{
    public DotNetSdkNotInstalledException()
        : base(".NET SDK is not installed or doesn't meet the minimum version requirement.")
    {
    }

    public DotNetSdkNotInstalledException(string message)
        : base(message)
    {
    }

    public DotNetSdkNotInstalledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
