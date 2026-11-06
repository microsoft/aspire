// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Xunit;
using DashboardError = Aspire.Dashboard.Components.Pages.Error;
using DashboardLogin = Aspire.Dashboard.Components.Pages.Login;

namespace Aspire.Dashboard.Components.Tests;

public class RoutedComponentAuthorizationTests
{
    // Pages that must be reachable before the browser has signed in. Adding a page here is a deliberate
    // decision to expose it without authentication.
    private static readonly HashSet<string> s_anonymousPageTypeNames =
    [
        typeof(DashboardLogin).FullName!,
        typeof(DashboardError).FullName!,
        // The legacy dashboard is optional (IncludeLegacyDashboard), so its types can't be referenced directly.
        "Aspire.Dashboard.Components.Legacy.Pages.Login",
        "Aspire.Dashboard.Components.Legacy.Pages.Error"
    ];

    [Fact]
    public void RoutedComponents_RequireFrontendPolicyUnlessExplicitlyAnonymous()
    {
        var routedComponents = GetRoutedComponents();

        Assert.NotEmpty(routedComponents);
        Assert.Contains(typeof(DashboardLogin), routedComponents);
        Assert.Contains(typeof(DashboardError), routedComponents);

        var violations = new List<string>();
        foreach (var component in routedComponents)
        {
            // AuthorizeRouteView and the endpoint authorization middleware both read the attributes on the component type.
            var attributes = component.GetCustomAttributes(inherit: true);
            var authorizeData = attributes.OfType<IAuthorizeData>().ToList();
            var allowsAnonymous = attributes.OfType<IAllowAnonymous>().Any();

            if (s_anonymousPageTypeNames.Contains(component.FullName!))
            {
                if (!allowsAnonymous || authorizeData.Count > 0)
                {
                    violations.Add($"{component.FullName} must be [AllowAnonymous] and must not have an authorization attribute.");
                }
            }
            else if (allowsAnonymous)
            {
                violations.Add($"{component.FullName} is [AllowAnonymous] but isn't in the list of anonymous pages.");
            }
            else if (authorizeData is not [{ Policy: FrontendAuthorizationDefaults.PolicyName, Roles: null, AuthenticationSchemes: null }])
            {
                violations.Add($"{component.FullName} must have exactly one [Authorize(Policy = {nameof(FrontendAuthorizationDefaults)}.{nameof(FrontendAuthorizationDefaults.PolicyName)})] attribute.");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Components_DoNotCombineAuthorizeAndAllowAnonymous()
    {
        // [AllowAnonymous] overrides [Authorize], so a type with both is contradictory and easy to misread.
        var conflictingTypes = typeof(DashboardWebApplication).Assembly.GetTypes()
            .Where(type =>
            {
                var attributes = type.GetCustomAttributes(inherit: true);
                return attributes.OfType<IAuthorizeData>().Any() && attributes.OfType<IAllowAnonymous>().Any();
            })
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(conflictingTypes);
    }

    private static List<Type> GetRoutedComponents()
    {
        return typeof(DashboardWebApplication).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(RouteAttribute), inherit: false))
            .ToList();
    }
}
