// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;

namespace Aspire.Dashboard.Authentication;

internal sealed class BrowserTokenAuthenticationOptions : AuthenticationSchemeOptions
{
    public bool Enabled { get; set; } = true;

    public string? Token
    {
        get;
        set
        {
            field = value;
            TokenBytes = value is not null ? Encoding.UTF8.GetBytes(value) : null;
        }
    }

    internal byte[]? TokenBytes { get; private set; }

    public string? SignInScheme { get; set; }
    public PathString LoginPath { get; set; } = "/login";
    public PathString TokenValidationPath { get; set; } = "/login/token";
    public PathString DefaultRedirectPath { get; set; } = "/";
    public string TokenParameter { get; set; } = "t";
    public string ReturnUrlParameter { get; set; } = "returnUrl";
    public bool IsPersistent { get; set; } = true;
    public IList<Claim> Claims { get; } = new List<Claim>();

    public override void Validate()
    {
        base.Validate();

        if (string.IsNullOrEmpty(SignInScheme))
        {
            throw new InvalidOperationException($"{nameof(SignInScheme)} must be configured, or the application must configure a default sign-in scheme or default scheme.");
        }

        ArgumentException.ThrowIfNullOrEmpty(TokenParameter);
        ArgumentException.ThrowIfNullOrEmpty(ReturnUrlParameter);

        if (Enabled && string.IsNullOrEmpty(Token))
        {
            throw new InvalidOperationException($"{nameof(Token)} must be configured when browser token authentication is enabled.");
        }

        if (!LoginPath.HasValue || !TokenValidationPath.HasValue || !DefaultRedirectPath.HasValue)
        {
            throw new InvalidOperationException($"{nameof(LoginPath)}, {nameof(TokenValidationPath)}, and {nameof(DefaultRedirectPath)} must be configured.");
        }
    }

    public override void Validate(string scheme)
    {
        base.Validate(scheme);

        if (string.Equals(SignInScheme, scheme, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{nameof(SignInScheme)} must not be the browser token authentication scheme.");
        }
    }
}
