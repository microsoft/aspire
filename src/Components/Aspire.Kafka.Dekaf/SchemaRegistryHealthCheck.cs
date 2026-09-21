// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf.SchemaRegistry;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Kafka.Dekaf;

internal sealed class SchemaRegistryHealthCheck(ISchemaRegistryClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Listing subjects exercises connectivity and authentication even for an empty registry.
        // This endpoint does not mutate schemas: https://docs.confluent.io/platform/current/schema-registry/develop/api.html#get--subjects
        await client.GetAllSubjectsAsync(cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Healthy();
    }
}
