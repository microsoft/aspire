// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Configuration;

public sealed class OtlpEndpointInfo(Func<ResolvedEndpointInfo?> grpcEndpoint, Func<ResolvedEndpointInfo?> httpEndpoint)
{
    public string? GrpcAddress => grpcEndpoint()?.GetResolvedAddress(replaceIPAnyWithLocalhost: true);

    public string? HttpAddress => httpEndpoint()?.GetResolvedAddress(replaceIPAnyWithLocalhost: true);
}
