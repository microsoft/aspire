// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

#pragma warning disable CS0618 // Azure Cache for Redis remains supported through its existing obsolete API.
internal sealed class AzureRedisCacheContainerResource(
    AzureRedisCacheResource owner,
    ParameterResource password)
    : RedisResource(owner.Name, password)
{
    public override ResourceAnnotationCollection Annotations => owner.Annotations;

    internal bool IsConfigured { get; set; }
}
#pragma warning restore CS0618

internal sealed class AzureManagedRedisContainerResource(
    AzureManagedRedisResource owner,
    ParameterResource password)
    : RedisResource(owner.Name, password)
{
    public override ResourceAnnotationCollection Annotations => owner.Annotations;

    internal bool IsConfigured { get; set; }
}
