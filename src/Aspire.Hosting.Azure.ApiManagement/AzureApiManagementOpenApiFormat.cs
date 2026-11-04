// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the OpenAPI document format imported into Azure API Management.
/// </summary>
public enum AzureApiManagementOpenApiFormat
{
    /// <summary>
    /// An OpenAPI document serialized as YAML.
    /// </summary>
    OpenApi,

    /// <summary>
    /// An OpenAPI document serialized as JSON.
    /// </summary>
    OpenApiJson,

    /// <summary>
    /// A Swagger 2.0 document serialized as JSON.
    /// </summary>
    SwaggerJson,
}
