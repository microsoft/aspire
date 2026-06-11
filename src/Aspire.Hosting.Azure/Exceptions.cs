// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal sealed class AzureCliNotOnPathException : DistributedApplicationException
{
    public AzureCliNotOnPathException() { }
    public AzureCliNotOnPathException(string message) : base(message) { }
    public AzureCliNotOnPathException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class MissingConfigurationException : DistributedApplicationException
{
    public MissingConfigurationException() { }
    public MissingConfigurationException(string message) : base(message) { }
    public MissingConfigurationException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class FailedToApplyEnvironmentException : DistributedApplicationException
{
    public FailedToApplyEnvironmentException() { }
    public FailedToApplyEnvironmentException(string message) : base(message) { }
    public FailedToApplyEnvironmentException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class ProvisioningFailedException : DistributedApplicationException
{
    public ProvisioningFailedException(string message, Exception inner) : base(message, inner) { }
}

