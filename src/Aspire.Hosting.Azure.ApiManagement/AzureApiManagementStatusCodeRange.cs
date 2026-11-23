// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an inclusive HTTP status-code range used by an API Management circuit breaker.
/// </summary>
/// <param name="Minimum">The lowest status code in the range.</param>
/// <param name="Maximum">The highest status code in the range.</param>
[AspireDto]
public sealed record AzureApiManagementStatusCodeRange(int Minimum, int Maximum);
