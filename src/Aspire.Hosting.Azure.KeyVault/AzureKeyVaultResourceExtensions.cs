// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using System.Text.RegularExpressions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure Key Vault resources to the application model.
/// </summary>
public static partial class AzureKeyVaultResourceExtensions
{
    /// <summary>
    /// Adds an Azure Key Vault resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// By default references to the Azure Key Vault resource will be assigned the following roles:
    /// </para>
    /// <para>
    /// - <see cref="KeyVaultBuiltInRole.KeyVaultAdministrator"/>
    /// </para>
    /// <para>
    /// These can be replaced by calling <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureKeyVaultResource}, KeyVaultBuiltInRole[])"/>.
    /// </para>
    /// <para>
    /// <strong>Managing Secrets:</strong>
    /// </para>
    /// <para>
    /// Use the <see cref="AddSecret(IResourceBuilder{AzureKeyVaultResource}, string, ParameterResource)"/> methods to add secrets to the Key Vault:
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var vault = builder.AddAzureKeyVault("vault");
    /// 
    /// // Add a secret from a parameter
    /// var secret = builder.AddParameter("secretParam", secret: true);
    /// vault.AddSecret("my-secret", secret);
    /// 
    /// // Add a secret from a reference expression
    /// var connectionString = ReferenceExpression.Create($"Server={server};Database={db}");
    /// vault.AddSecret("connection-string", connectionString);
    /// 
    /// // Get a reference to an existing secret
    /// var existingSecret = vault.GetSecret("existing-secret");
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds an Azure Key Vault resource")]
    public static IResourceBuilder<AzureKeyVaultResource> AddAzureKeyVault(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = static (AzureResourceInfrastructure infrastructure) =>
        {
            var azureResource = (AzureKeyVaultResource)infrastructure.AspireResource;

            // Check if this Key Vault has a private endpoint (via annotation)
            var hasPrivateEndpoint = azureResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

            var keyVault = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
            (identifier, name) =>
            {
                var resource = KeyVaultService.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                var kv = new KeyVaultService(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Properties = new KeyVaultProperties()
                    {
                        TenantId = BicepFunction.GetTenant().TenantId,
                        Sku = new KeyVaultSku()
                        {
                            Family = KeyVaultSkuFamily.A,
                            Name = KeyVaultSkuName.Standard
                        },
                        EnableRbacAuthorization = true,
                    },
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                };

                // When using private endpoints, disable public network access.
                if (hasPrivateEndpoint)
                {
                    kv.Properties.PublicNetworkAccess = "Disabled";
                }

                return kv;
            });

            infrastructure.Add(new ProvisioningOutput("vaultUri", typeof(string))
            {
                Value = keyVault.Properties.VaultUri.ToBicepExpression()
            });

            // Process all secret resources
            foreach (var secretResource in azureResource.Secrets)
            {
                var value = secretResource.Value as IManifestExpressionProvider ?? throw new NotSupportedException(
                    $"Secret value for '{secretResource.SecretName}' is an unsupported type.");

                var paramValue = value.AsProvisioningParameter(infrastructure, isSecure: true);

                var secret = new KeyVaultSecret(Infrastructure.NormalizeBicepIdentifier($"secret_{secretResource.SecretName}"))
                {
                    Name = secretResource.SecretName,
                    Properties = new SecretProperties
                    {
                        Value = paramValue
                    },
                    Parent = keyVault,
                };

                infrastructure.Add(secret);
            }

            // We need to output name to externalize role assignments.
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = keyVault.Name.ToBicepExpression() });

            // Output the resource id for private endpoint support.
            infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = keyVault.Id.ToBicepExpression() });
        };

        var resource = new AzureKeyVaultResource(name, configureInfrastructure);
        return builder.AddResource(resource)
            .WithDefaultRoleAssignments(KeyVaultBuiltInRole.GetBuiltInRoleName,
                KeyVaultBuiltInRole.KeyVaultSecretsUser);
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Key Vault resource. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure Key Vault resource.</param>
    /// <param name="roles">The built-in Key Vault roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <remarks>
    /// <example>
    /// Assigns the KeyVaultReader role to the 'Projects.Api' project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var vault = builder.AddAzureKeyVault("vault");
    /// 
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithRoleAssignments(vault, KeyVaultBuiltInRole.KeyVaultReader)
    ///   .WithReference(vault);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "KeyVaultBuiltInRole is an Azure.Provisioning type not compatible with ATS. Use the AzureKeyVaultRole-based overload instead.")]
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureKeyVaultResource> target,
        params KeyVaultBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, KeyVaultBuiltInRole.GetBuiltInRoleName, roles);
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure Key Vault resource. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure Key Vault resource.</param>
    /// <param name="roles">The Key Vault roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="AzureKeyVaultRole"/> value.</exception>
    [AspireExport("withKeyVaultRoleAssignments", Description = "Assigns Key Vault roles to a resource")]
    internal static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureKeyVaultResource> target,
        params AzureKeyVaultRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, Array.Empty<KeyVaultBuiltInRole>());
        }

        var builtInRoles = new KeyVaultBuiltInRole[roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                AzureKeyVaultRole.KeyVaultAdministrator => KeyVaultBuiltInRole.KeyVaultAdministrator,
                AzureKeyVaultRole.KeyVaultCertificateUser => KeyVaultBuiltInRole.KeyVaultCertificateUser,
                AzureKeyVaultRole.KeyVaultCertificatesOfficer => KeyVaultBuiltInRole.KeyVaultCertificatesOfficer,
                AzureKeyVaultRole.KeyVaultContributor => KeyVaultBuiltInRole.KeyVaultContributor,
                AzureKeyVaultRole.KeyVaultCryptoOfficer => KeyVaultBuiltInRole.KeyVaultCryptoOfficer,
                AzureKeyVaultRole.KeyVaultCryptoServiceEncryptionUser => KeyVaultBuiltInRole.KeyVaultCryptoServiceEncryptionUser,
                AzureKeyVaultRole.KeyVaultCryptoServiceReleaseUser => KeyVaultBuiltInRole.KeyVaultCryptoServiceReleaseUser,
                AzureKeyVaultRole.KeyVaultCryptoUser => KeyVaultBuiltInRole.KeyVaultCryptoUser,
                AzureKeyVaultRole.KeyVaultDataAccessAdministrator => KeyVaultBuiltInRole.KeyVaultDataAccessAdministrator,
                AzureKeyVaultRole.KeyVaultReader => KeyVaultBuiltInRole.KeyVaultReader,
                AzureKeyVaultRole.KeyVaultSecretsOfficer => KeyVaultBuiltInRole.KeyVaultSecretsOfficer,
                AzureKeyVaultRole.KeyVaultSecretsUser => KeyVaultBuiltInRole.KeyVaultSecretsUser,
                AzureKeyVaultRole.ManagedHsmContributor => KeyVaultBuiltInRole.ManagedHsmContributor,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(AzureKeyVaultRole)} value.", nameof(roles))
            };
        }

        return builder.WithRoleAssignments(target, builtInRoles);
    }

    /// <summary>
    /// Gets a secret reference for the specified secret name from the Azure Key Vault resource.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="secretName">The name of the secret.</param>
    /// <returns>A reference to the secret.</returns>
    [AspireExport(Description = "Gets a secret reference from the Azure Key Vault")]
    public static IAzureKeyVaultSecretReference GetSecret(this IResourceBuilder<AzureKeyVaultResource> builder, string secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Resource.GetSecret(secretName);
    }

    /// <summary>
    /// Adds a secret to the Azure Key Vault resource with the value from a parameter resource.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="name">The name of the secret. Must follow Azure Key Vault naming rules.</param>
    /// <param name="parameterResource">The parameter resource containing the secret value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a secret to the Azure Key Vault from a parameter resource")]
    public static IResourceBuilder<AzureKeyVaultSecretResource> AddSecret(this IResourceBuilder<AzureKeyVaultResource> builder, string name, IResourceBuilder<ParameterResource> parameterResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameterResource);

        return builder.AddSecret(name, name, parameterResource.Resource);
    }

    /// <summary>
    /// Adds a secret to the Azure Key Vault resource with the value from a parameter resource.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="name">The name of the secret. Must follow Azure Key Vault naming rules.</param>
    /// <param name="parameterResource">The parameter resource containing the secret value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "Raw ParameterResource overload; use the IResourceBuilder<ParameterResource> variant instead.")]
    public static IResourceBuilder<AzureKeyVaultSecretResource> AddSecret(this IResourceBuilder<AzureKeyVaultResource> builder, string name, ParameterResource parameterResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameterResource);

        ValidateSecretName(name);

        var secret = new AzureKeyVaultSecretResource(name, name, builder.Resource, parameterResource);
        builder.Resource.Secrets.Add(secret);

        return builder.ApplicationBuilder.AddResource(secret).ExcludeFromManifest();
    }

    /// <summary>
    /// Adds a secret to the Azure Key Vault resource with the value from a reference expression.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="name">The name of the secret. Must follow Azure Key Vault naming rules.</param>
    /// <param name="value">The reference expression containing the secret value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addSecretFromExpression", Description = "Adds a secret to the Azure Key Vault from a reference expression")]
    public static IResourceBuilder<AzureKeyVaultSecretResource> AddSecret(this IResourceBuilder<AzureKeyVaultResource> builder, string name, ReferenceExpression value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);

        ValidateSecretName(name);

        var secret = new AzureKeyVaultSecretResource(name, name, builder.Resource, value);
        builder.Resource.Secrets.Add(secret);

        return builder.ApplicationBuilder.AddResource(secret).ExcludeFromManifest();
    }

    /// <summary>
    /// Adds a secret to the Azure Key Vault resource with the value from a parameter resource.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="name">The name of the secret resource.</param>
    /// <param name="secretName">The name of the secret. Must follow Azure Key Vault naming rules.</param>
    /// <param name="parameterResource">The parameter resource containing the secret value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addSecretWithName", Description = "Adds a named secret to the Azure Key Vault from a parameter resource")]
    public static IResourceBuilder<AzureKeyVaultSecretResource> AddSecret(this IResourceBuilder<AzureKeyVaultResource> builder, [ResourceName] string name, string secretName, IResourceBuilder<ParameterResource> parameterResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameterResource);

        return builder.AddSecret(name, secretName, parameterResource.Resource);
    }

    /// <summary>
    /// Adds a secret to the Azure Key Vault resource with the value from a parameter resource.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="name">The name of the secret resource.</param>
    /// <param name="secretName">The name of the secret. Must follow Azure Key Vault naming rules.</param>
    /// <param name="parameterResource">The parameter resource containing the secret value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "Raw ParameterResource overload; use the IResourceBuilder<ParameterResource> variant instead.")]
    public static IResourceBuilder<AzureKeyVaultSecretResource> AddSecret(this IResourceBuilder<AzureKeyVaultResource> builder, [ResourceName] string name, string secretName, ParameterResource parameterResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(parameterResource);

        ValidateSecretName(secretName);

        var secret = new AzureKeyVaultSecretResource(name, secretName, builder.Resource, parameterResource);
        builder.Resource.Secrets.Add(secret);

        return builder.ApplicationBuilder.AddResource(secret).ExcludeFromManifest();
    }

    /// <summary>
    /// Adds a secret to the Azure Key Vault resource with the value from a reference expression.
    /// </summary>
    /// <param name="builder">The Azure Key Vault resource builder.</param>
    /// <param name="name">The name of the secret resource.</param>
    /// <param name="secretName">The name of the secret. Must follow Azure Key Vault naming rules.</param>
    /// <param name="value">The reference expression containing the secret value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addSecretWithNameFromExpression", Description = "Adds a named secret to the Azure Key Vault from a reference expression")]
    public static IResourceBuilder<AzureKeyVaultSecretResource> AddSecret(this IResourceBuilder<AzureKeyVaultResource> builder, [ResourceName] string name, string secretName, ReferenceExpression value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(value);

        ValidateSecretName(secretName);

        var secret = new AzureKeyVaultSecretResource(name, secretName, builder.Resource, value);
        builder.Resource.Secrets.Add(secret);

        return builder.ApplicationBuilder.AddResource(secret).ExcludeFromManifest();
    }

    private static void ValidateSecretName(string secretName)
    {
        // Azure Key Vault secret names must be 1-127 characters long and contain only ASCII letters (a-z, A-Z), digits (0-9), and dashes (-)
        if (secretName.Length > 127)
        {
            throw new ArgumentException("Secret name cannot be longer than 127 characters.", nameof(secretName));
        }

        if (!AzureKeyVaultSecretNameRegex().IsMatch(secretName))
        {
            throw new ArgumentException("Secret name can only contain ASCII letters (a-z, A-Z), digits (0-9), and dashes (-).", nameof(secretName));
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9-]+$")]
    private static partial Regex AzureKeyVaultSecretNameRegex();
}
