// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Specifies the OpenAPI document format imported into Azure API Management.
/// </summary>
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
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
