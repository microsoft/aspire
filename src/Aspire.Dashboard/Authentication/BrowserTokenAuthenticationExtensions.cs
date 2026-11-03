// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Authentication;

internal static class BrowserTokenAuthenticationExtensions
{
    public static AuthenticationBuilder AddBrowserToken(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        Action<BrowserTokenAuthenticationOptions> configureOptions)
    {
        return builder.AddBrowserToken(authenticationScheme, displayName: null, configureOptions);
    }

    public static AuthenticationBuilder AddBrowserToken(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        string? displayName,
        Action<BrowserTokenAuthenticationOptions> configureOptions)
    {
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<BrowserTokenAuthenticationOptions>, PostConfigureBrowserTokenAuthenticationOptions>());

        return builder.AddScheme<BrowserTokenAuthenticationOptions, BrowserTokenAuthenticationHandler>(authenticationScheme, displayName, configureOptions);
    }

    private sealed class PostConfigureBrowserTokenAuthenticationOptions(IOptions<AuthenticationOptions> authenticationOptions)
        : IPostConfigureOptions<BrowserTokenAuthenticationOptions>
    {
        public void PostConfigure(string? name, BrowserTokenAuthenticationOptions options)
        {
            // Match AuthenticationBuilder.AddRemoteScheme: the application chooses how
            // to persist identities, and an explicitly configured scheme takes precedence.
            // https://github.com/dotnet/aspnetcore/blob/main/src/Security/Authentication/Core/src/AuthenticationBuilder.cs
            options.SignInScheme ??= authenticationOptions.Value.DefaultSignInScheme ?? authenticationOptions.Value.DefaultScheme;
        }
    }
}
