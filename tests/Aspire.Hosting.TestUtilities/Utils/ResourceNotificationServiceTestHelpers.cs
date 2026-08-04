// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Utils;

public static class ResourceNotificationServiceTestHelpers
{
    public static ResourceNotificationService Create(ILogger<ResourceNotificationService>? logger = null, IHostApplicationLifetime? hostApplicationLifetime = null, ResourceLoggerService? resourceLoggerService = null)
    {
        return new ResourceNotificationService(
            logger ?? new NullLogger<ResourceNotificationService>(),
            hostApplicationLifetime ?? new TestHostApplicationLifetime(),
            TestServiceProvider.Instance,
            resourceLoggerService ?? new ResourceLoggerService()
            );
    }
}
