// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.AppService;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting.Azure.AppService;

/// <summary>
/// A derived <see cref="global::Azure.Provisioning.AppService.SiteContainer"/> that supports adding the @onlyIfNotExists() decorator
/// to the generated Bicep resource statement.
/// </summary>
internal sealed class SiteContainer : global::Azure.Provisioning.AppService.SiteContainer
{
    private readonly bool _addOnlyIfNotExistsDecorator;

    /// <summary>
    /// Initializes a new instance of <see cref="SiteContainer"/>.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep identifier name of the resource.</param>
    /// <param name="addOnlyIfNotExistsDecorator">
    /// When <c>true</c>, adds the @onlyIfNotExists() decorator to the resource statement.
    /// </param>
    public SiteContainer(string bicepIdentifier, bool addOnlyIfNotExistsDecorator = false)
        : base(bicepIdentifier)
    {
        _addOnlyIfNotExistsDecorator = addOnlyIfNotExistsDecorator;
    }

    /// <inheritdoc />
    protected override IEnumerable<BicepStatement> Compile()
    {
        foreach (var statement in base.Compile())
        {
            if (_addOnlyIfNotExistsDecorator && statement is ResourceStatement resourceStatement)
            {
                // Add @onlyIfNotExists() decorator to the resource statement
                // Using FunctionCallExpression to generate "onlyIfNotExists()" with parentheses
                resourceStatement.Decorators.Add(
                    new DecoratorExpression(new FunctionCallExpression(new IdentifierExpression("onlyIfNotExists"))));
            }

            yield return statement;
        }
    }
}

/// <summary>
/// A derived <see cref="WebSite"/> that supports adding the @onlyIfNotExists() decorator
/// to the generated Bicep resource statement.
/// </summary>
internal sealed class AspireWebSite : WebSite
{
    private readonly bool _addOnlyIfNotExistsDecorator;

    /// <summary>
    /// Initializes a new instance of <see cref="AspireWebSite"/>.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep identifier name of the resource.</param>
    /// <param name="addOnlyIfNotExistsDecorator">
    /// When <c>true</c>, adds the @onlyIfNotExists() decorator to the resource statement.
    /// </param>
    public AspireWebSite(string bicepIdentifier, bool addOnlyIfNotExistsDecorator = false)
        : base(bicepIdentifier)
    {
        _addOnlyIfNotExistsDecorator = addOnlyIfNotExistsDecorator;
    }

    /// <inheritdoc />
    protected override IEnumerable<BicepStatement> Compile()
    {
        foreach (var statement in base.Compile())
        {
            if (_addOnlyIfNotExistsDecorator && statement is ResourceStatement resourceStatement)
            {
                // Add @onlyIfNotExists() decorator to the resource statement
                // Using FunctionCallExpression to generate "onlyIfNotExists()" with parentheses
                resourceStatement.Decorators.Add(
                    new DecoratorExpression(new FunctionCallExpression(new IdentifierExpression("onlyIfNotExists"))));
            }

            yield return statement;
        }
    }
}

/// <summary>
/// A derived <see cref="global::Azure.Provisioning.AppService.SiteNetworkConfig"/> that emits the required
/// singleton resource name.
/// </summary>
internal sealed class AspireSiteNetworkConfig : global::Azure.Provisioning.AppService.SiteNetworkConfig
{
    /// <summary>
    /// Initializes a new instance of <see cref="AspireSiteNetworkConfig"/>.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep identifier name of the resource.</param>
    public AspireSiteNetworkConfig(string bicepIdentifier)
        : base(bicepIdentifier)
    {
    }

    /// <inheritdoc />
    protected override IEnumerable<BicepStatement> Compile()
    {
        foreach (var statement in base.Compile())
        {
            if (statement is ResourceStatement resourceStatement &&
                resourceStatement.Body is ObjectExpression body &&
                !body.Properties.Any(static property => property.Name is "name"))
            {
                // Workaround for https://github.com/Azure/azure-sdk-for-net/issues/54629.
                // SiteNetworkConfig models its fixed "virtualNetwork" name as an output, so the
                // generated resource omits the required Bicep property. Bicep requires a name even
                // for this singleton child and otherwise reports BCP035. Emit the fixed App Service
                // child name explicitly until the Azure.Provisioning.AppService version consumed by
                // Aspire emits the default name as a resource property.
                var resourceWithName = new ResourceStatement(
                    resourceStatement.Name,
                    resourceStatement.Type,
                    new ObjectExpression(
                    [
                        new PropertyExpression("name", new StringLiteralExpression("virtualNetwork")),
                        .. body.Properties
                    ]))
                {
                    Existing = resourceStatement.Existing
                };

                foreach (var decorator in resourceStatement.Decorators)
                {
                    resourceWithName.Decorators.Add(decorator);
                }

                yield return resourceWithName;
                continue;
            }

            yield return statement;
        }
    }
}
