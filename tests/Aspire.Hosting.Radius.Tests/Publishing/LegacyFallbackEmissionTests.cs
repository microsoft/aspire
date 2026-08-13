// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Tests for L2: legacy parent emission.
///
/// When a resource falls back to a legacy <c>Applications.*</c> type, it must
/// be parented to <c>Applications.Core/environments</c> + <c>Applications.Core/applications</c>
/// rather than the <c>Radius.Core/*</c> UDT parents. The UDT pair and the legacy
/// pair share the same resource <c>name:</c> values; only the Bicep identifiers differ.
///
/// These scenarios snapshot the full generated Bicep (via Verify) rather than sampling
/// individual tokens: the previous per-token <c>Assert.Contains</c>/<c>Assert.DoesNotContain</c>
/// checks proved presence/absence of a handful of strings but could pass while the surrounding
/// document was malformed. The verified snapshot pins the entire structure - parent pairs,
/// identifiers (<c>myenv</c> vs <c>myenv_legacy</c>, <c>app</c> vs <c>app_legacy</c>), recipe
/// schema keys, per-resource parent references, and recipe-pack registration - in one place.
/// </summary>
public class LegacyFallbackEmissionTests
{
    private static string GenerateBicep(Action<IDistributedApplicationBuilder> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        configure(builder);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        return context.GenerateBicep(model);
    }

    /// <summary>
    /// Legacy-only publish (Redis falls back to <c>Applications.Datastores/redisCaches</c>).
    /// Guards: legacy parents emitted with inline legacy-schema recipes (<c>templateKind</c>/
    /// <c>templatePath</c>) and default recipe template; UDT parents and recipe pack absent; the
    /// legacy env/app take the unsuffixed <c>myenv</c>/<c>app</c> identifiers; legacy env carries
    /// <c>compute.kind: 'kubernetes'</c>.
    /// </summary>
    [Fact]
    public Task RedisOnly_EmitsLegacyParentsAndNoUdtResources()
    {
        var bicep = GenerateBicep(b => b.AddRedis("cache"));
        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// Mixed legacy + UDT + container. Guards: both UDT (<c>Radius.Core/*</c>) and legacy
    /// (<c>Applications.Core/*</c>) parent pairs present; UDT recipe pack uses the new schema
    /// (<c>recipeKind</c>/<c>recipeLocation</c>) while the legacy env uses the legacy schema
    /// (<c>templateKind</c>/<c>templatePath</c>).
    /// </summary>
    [Fact]
    public Task MixedLegacyAndUdt_EmitsBothParentPairs()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddRedis("cache");     // legacy fallback -> Applications.Datastores/redisCaches
            b.AddPostgres("db");     // UDT -> Radius.Data/postgreSQL (or similar)
            b.AddContainer("api", "myapp/api", "latest");
        });
        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// Legacy + UDT with no container. Guards: the UDT env (<c>resource myenv</c>) and the legacy
    /// env (<c>resource myenv_legacy</c>) coexist and both emit <c>name: 'myenv'</c>; the legacy
    /// resource (<c>cache</c>) references the legacy parents (<c>myenv_legacy.id</c>/
    /// <c>app_legacy.id</c>) while the UDT resource (<c>db</c>) references the UDT parents and never
    /// the legacy ones.
    /// </summary>
    [Fact]
    public Task LegacyAndUdt_ShareResourceNameAndUseDistinctParents()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddRedis("cache");
            b.AddPostgres("db");
        });
        return Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// UDT-only publish (Postgres + container, no legacy fallback). Guards: legacy parents and
    /// <c>_legacy</c> identifiers are absent; the UDT parent pair is still emitted.
    /// </summary>
    [Fact]
    public Task UdtOnly_DoesNotEmitLegacyParents()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddPostgres("db");
            b.AddContainer("api", "myapp/api", "latest");
        });
        return Verify(bicep, extension: "bicep");
    }

}
