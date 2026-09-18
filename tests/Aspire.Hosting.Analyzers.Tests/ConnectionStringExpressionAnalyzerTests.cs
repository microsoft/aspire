// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using static Microsoft.CodeAnalysis.Testing.DiagnosticResult;

namespace Aspire.Hosting.Analyzers.Tests;

public class ConnectionStringExpressionAnalyzerTests
{
    [Fact]
    public async Task DirectInterfaceAccessReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringExpressionMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
                => resource.ConnectionStringExpression;
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(4, 17)
                    .WithMessage("Direct access to 'ConnectionStringExpression' may ignore a selected resource projection. Use 'GetConnectionStringExpression()' to prefer the projection, or pass 'preferOwner: true' when owner precedence is intentional.")
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ConcreteImplementationAccessReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringExpressionMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(CustomResource resource)
                => resource.ConnectionStringExpression;

            sealed class CustomResource(string name) : Resource(name), IResourceWithConnectionString
            {
                public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Empty;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(4, 17)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task GenericCapabilityAccessReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringExpressionMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression<T>(T resource)
                where T : IResourceWithConnectionString
                => resource.ConnectionStringExpression;
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(5, 17)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ProjectionAwareAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
                => resource
                    .GetEffectiveCapability<IResourceWithConnectionString>()!
                    .ConnectionStringExpression;

            static ReferenceExpression GetOwnerExpression(IResourceWithConnectionString resource)
                => resource
                    .GetEffectiveCapability<IResourceWithConnectionString>(preferOwner: true)!
                    .ConnectionStringExpression;

            static ReferenceExpression GetExpressionWithAccessor(IResourceWithConnectionString resource)
                => resource.GetConnectionStringExpression();

            static ReferenceExpression GetOwnerExpressionWithAccessor(IResourceWithConnectionString resource)
                => resource.GetConnectionStringExpression(preferOwner: true);
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ProjectionAwareLocalAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                return effective.ConnectionStringExpression;
            }

            static ReferenceExpression GetOwnerExpression(IResourceWithConnectionString resource)
            {
                var owner = resource.GetEffectiveCapability<IResourceWithConnectionString>(preferOwner: true)!;
                return owner.ConnectionStringExpression;
            }
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task ReassignedProjectionAwareLocalReportsDiagnostic()
    {
        var diagnostic = AppHostAnalyzer.Diagnostics.s_connectionStringExpressionMustBeResolved;

        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResourceWithConnectionString resource)
            {
                var effective = resource.GetEffectiveCapability<IResourceWithConnectionString>()!;
                effective = resource;
                return effective.ConnectionStringExpression;
            }
            """,
            [
                CompilerWarning(diagnostic.Id)
                    .WithLocation(7, 22)
            ]);

        await test.RunAsync();
    }

    [Fact]
    public async Task ExplicitOwnerAccessReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(IResource resource)
                => ((IResourceWithConnectionString)resource.GetOwnerOrSelf())
                    .ConnectionStringExpression;
            """,
            []);

        await test.RunAsync();
    }

    [Fact]
    public async Task UnrelatedConcretePropertyReportsNoDiagnostic()
    {
        var test = AnalyzerTest.Create<AppHostAnalyzer>("""
            using Aspire.Hosting.ApplicationModel;

            static ReferenceExpression GetExpression(CustomResource resource)
                => resource.ConnectionStringExpression;

            sealed class CustomResource(string name) : Resource(name)
            {
                public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Empty;
            }
            """,
            []);

        await test.RunAsync();
    }
}
