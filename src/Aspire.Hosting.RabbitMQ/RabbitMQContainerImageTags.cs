// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ;

internal static class RabbitMQContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/rabbitmq</remarks>
    public const string Image = "library/rabbitmq";

    /// <remarks>4.2</remarks>
    public const string Tag = "4.2";

    /// <remarks><inheritdoc cref="Tag"/>-management</remarks>
    public const string ManagementTag = $"{Tag}-management";
}
