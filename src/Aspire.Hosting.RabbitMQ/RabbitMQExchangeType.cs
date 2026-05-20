// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies the type of a RabbitMQ exchange resource.
/// </summary>
public enum RabbitMQExchangeType
{
    /// <summary>Direct exchange type.</summary>
    Direct,

    /// <summary>Topic exchange type.</summary>
    Topic,

    /// <summary>Fanout exchange type.</summary>
    Fanout,

    /// <summary>Headers exchange type.</summary>
    Headers
}
