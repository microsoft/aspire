// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http.Metadata;

namespace Aspire.Dashboard.Utils;

internal static class RoutingExtensions
{
    public static TBuilder SkipStatusCodePages<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.Metadata.Add(new SkipStatusCodePagesAttribute());
        });
        return builder;
    }

    public static IEndpointConventionBuilder MapPostNotFound(this IEndpointRouteBuilder endpoints, string pattern)
    {
        return endpoints.MapPost(pattern, context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }

    public static IEndpointConventionBuilder MapGetNotFound(this IEndpointRouteBuilder endpoints, string pattern)
    {
        return endpoints.MapGet(pattern, context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }
}

/// <summary>
/// Prevents status code pages from replacing endpoint error responses.
/// </summary>
// Remove this type and use Microsoft.AspNetCore.Mvc.SkipStatusCodePagesAttribute when
// https://github.com/dotnet/aspnetcore/issues/69217 is fixed.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
internal sealed class SkipStatusCodePagesAttribute : Attribute, ISkipStatusCodePagesMetadata
{
}
