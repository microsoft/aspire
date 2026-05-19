// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding parameter resources to an application.
/// </summary>
public static class ParameterResourceBuilderExtensions
{
    /// <summary>
    /// Adds a parameter resource to the application.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="secret">Optional flag indicating whether the parameter should be regarded as secret.</param>
    /// <returns>Resource builder for the parameter.</returns>
    /// <exception cref="DistributedApplicationException"></exception>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addParameter dispatcher export.")]
    public static IResourceBuilder<ParameterResource> AddParameter(this IDistributedApplicationBuilder builder, [ResourceName] string name, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.AddParameter(
                new ParameterResource(
                    name,
                    parameterDefault => GetParameterValue(builder.Configuration, name, parameterDefault),
                    secret)
                );
    }

    [AspireExport("addParameter", Description = "Adds a parameter resource")]
    internal static IResourceBuilder<ParameterResource> AddParameterForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? value = null,
        bool publishValueAsDefault = false,
        bool secret = false)
    {
        if (value is null && publishValueAsDefault)
        {
            throw new ArgumentException("A parameter value is required when publishValueAsDefault is true.", nameof(publishValueAsDefault));
        }

        return value is null
            ? builder.AddParameter(name, secret)
            : builder.AddParameter(name, value, publishValueAsDefault, secret);
    }

    /// <summary>
    /// Adds a parameter resource to the application with a given value.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="value">A string value to use for the parameter</param>
    /// <param name="publishValueAsDefault">Indicates whether the value should be published to the manifest. This is not meant for sensitive data.</param>
    /// <param name="secret">Optional flag indicating whether the parameter should be regarded as secret.</param>
    /// <returns>Resource builder for the parameter.</returns>
    /// <remarks>publishValueAsDefault and secret are mutually exclusive.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
                                                     Justification = "third parameters are mutually exclusive.")]
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addParameter dispatcher export.")]
    public static IResourceBuilder<ParameterResource> AddParameter(this IDistributedApplicationBuilder builder, [ResourceName] string name, string value, bool publishValueAsDefault = false, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        return builder.AddParameter(name, () => value, publishValueAsDefault, secret);
    }

    /// <summary>
    /// Adds a parameter resource to the application with a value coming from a callback function.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="valueGetter">A callback function that returns the value of the parameter</param>
    /// <param name="publishValueAsDefault">Indicates whether the value should be published to the manifest. This is not meant for sensitive data.</param>
    /// <param name="secret">Optional flag indicating whether the parameter should be regarded as secret.</param>
    /// <returns>Resource builder for the parameter.</returns>
    /// <remarks>
    /// publishValueAsDefault and secret are mutually exclusive.
    /// <para>This method is not available in polyglot app hosts. Use the overload with a string value instead.</para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
                                                     Justification = "third parameters are mutually exclusive.")]
    [AspireExportIgnore(Reason = "Raw Func<string> delegate — not ATS-compatible.")]
    public static IResourceBuilder<ParameterResource> AddParameter(this IDistributedApplicationBuilder builder, string name, Func<string> valueGetter, bool publishValueAsDefault = false, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(valueGetter);

        // We don't allow a parameter to be both secret and published, as that would write the secret to the manifest.
        if (publishValueAsDefault && secret)
        {
            throw new ArgumentException("A parameter cannot be both secret and published as a default value.", nameof(secret));
        }

        // If publishValueAsDefault is set, we wrap the valueGetter in a ConstantParameterDefault, which gives
        // us both the runtime value and the value to publish to the manifest.
        // Otherwise, we just use the valueGetter directly, which only gives us the runtime value.
        return builder.AddParameter(
                new ParameterResource(
                    name,
                    parameterDefault => parameterDefault is not null ? parameterDefault.GetDefaultValue() : valueGetter(),
                    secret)
                {
                    Default = publishValueAsDefault ? new ConstantParameterDefault(valueGetter) : null
                });
    }

    /// <summary>
    /// Adds a parameter resource to the application, with a value coming from configuration.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="configurationKey">Configuration key used to get the value of the parameter</param>
    /// <param name="secret">Optional flag indicating whether the parameter should be regarded as secret.</param>
    /// <returns>Resource builder for the parameter.</returns>
    [AspireExport(Description = "Adds a parameter sourced from configuration")]
    public static IResourceBuilder<ParameterResource> AddParameterFromConfiguration(this IDistributedApplicationBuilder builder, string name, string configurationKey, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configurationKey);

        return builder.AddParameter(
                new ParameterResource(
                    name,
                    parameterDefault => GetParameterValue(builder.Configuration, name, parameterDefault, configurationKey),
                    secret)
                {
                    ConfigurationKey = configurationKey
                });
    }

    /// <summary>
    /// Adds a parameter resource to the application, with a value coming from a <see cref="ParameterDefault"/> if not supplied from configuration.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="value">A <see cref="ParameterDefault"/> that is used to provide the parameter value if a value is not present in configuration</param>
    /// <param name="secret">Optional flag indicating whether the parameter should be regarded as secret.</param>
    /// <param name="persist">Persist the value to the app host project's user secrets store. This is typically
    /// done when the value is generated, so that it stays stable across runs. This is only relevant when
    /// <see cref="DistributedApplicationExecutionContext.IsRunMode"/> is <c>true</c>
    /// </param>
    /// <returns>Resource builder for the parameter.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use the overload with a string value instead.</remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
                                                     Justification = "third parameters are mutually exclusive.")]
    [AspireExportIgnore(Reason = "ParameterDefault is not an ATS-exported type.")]
    public static IResourceBuilder<ParameterResource> AddParameter(this IDistributedApplicationBuilder builder, [ResourceName] string name, ParameterDefault value, bool secret = false, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        // If it needs persistence, wrap it in a UserSecretsParameterDefault
        if (persist && builder.ExecutionContext.IsRunMode)
        {
            value = new UserSecretsParameterDefault(builder.Environment.ApplicationName, name, value, builder.UserSecretsManager);
        }

        return builder.AddParameter(
            new ParameterResource(name, p => GetParameterValue(builder.Configuration, name, value), secret)
            {
                Default = value
            });
    }

    [AspireExport("addParameterWithGeneratedValue", Description = "Adds a parameter with a generated default value")]
    internal static IResourceBuilder<ParameterResource> AddParameterWithGeneratedValueForPolyglot(this IDistributedApplicationBuilder builder, [ResourceName] string name, GenerateParameterDefault value, bool secret = false, bool persist = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        return builder.AddParameter(name, (ParameterDefault)value, secret, persist);
    }

    /// <summary>
    /// Sets the description of the parameter resource.
    /// </summary>
    /// <param name="builder">Resource builder for the parameter.</param>
    /// <param name="description">The parameter description.</param>
    /// <param name="enableMarkdown">
    /// A value indicating whether the description should be rendered as Markdown.
    /// <c>true</c> allows the description to contain Markdown elements such as links, text decoration and lists.
    /// </param>
    /// <returns>Resource builder for the parameter.</returns>
    [AspireExport(Description = "Sets a parameter description")]
    public static IResourceBuilder<ParameterResource> WithDescription(this IResourceBuilder<ParameterResource> builder, string description, bool enableMarkdown = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(description);

        builder.Resource.Description = description;
        builder.Resource.EnableDescriptionMarkdown = enableMarkdown;

        return builder;
    }

    /// <summary>
    /// Sets a custom input generator function for the parameter resource.
    /// </summary>
    /// <param name="builder">Resource builder for the parameter.</param>
    /// <param name="createInput">Function to customize the input for the parameter.</param>
    /// <returns>Resource builder for the parameter.</returns>
    /// <remarks>
    /// Use this method to customize how the input field for this parameter is rendered when its value is requested, e.g.:
    /// <code language="csharp">
    /// builder.AddParameter("external-service-url")
    ///     .WithCustomInput(parameter => new()
    ///     {
    ///         InputType = parameter.Secret ? InputType.SecretText : InputType.Text,
    ///         Value = "https://example.com",
    ///         Label = parameter.Name,
    ///         Placeholder = $"Enter value for {parameter.Name}",
    ///         Description = parameter.Description
    ///     });
    /// </code>
    /// <para>This method is not available in polyglot app hosts.</para>
    /// </remarks>
    [Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Complex Func delegate with InteractionInput — not ATS-compatible.")]
    public static IResourceBuilder<ParameterResource> WithCustomInput(this IResourceBuilder<ParameterResource> builder, Func<ParameterResource, InteractionInput> createInput)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createInput);

        builder.Resource.Annotations.Add(new InputGeneratorAnnotation(createInput));

        return builder;
    }

    // Internal to allow ParameterProcessor to check for configured values
    // without triggering default value generation
    internal static string GetParameterValue(IConfiguration configuration, string name, ParameterDefault? parameterDefault, string? configurationKey = null)
    {
        configurationKey ??= $"Parameters:{name}";
        
        // Use the shared helper to get the value with normalization support
        var value = configuration.GetValueWithNormalizedKey(configurationKey);
        
        return value
            ?? parameterDefault?.GetDefaultValue()
            ?? throw new MissingParameterValueException($"Parameter resource could not be used because configuration key '{configurationKey}' is missing and the Parameter has no default value.");
    }

    internal static IResourceBuilder<T> AddParameter<T>(this IDistributedApplicationBuilder builder, T resource)
        where T : ParameterResource
    {
        var state = new CustomResourceSnapshot
        {
            ResourceType = KnownResourceTypes.Parameter,
            Properties = [
                new(CustomResourceKnownProperties.Source, resource.ConfigurationKey)
            ],
            State = KnownResourceStates.Waiting
        };

        return builder.AddResource(resource)
                      .WithInitialState(state);
    }
    /// <summary>
    /// Adds a parameter to the distributed application but wrapped in a resource with a connection string for use with <see cref="ResourceBuilderExtensions.WithReference{TDestination}(IResourceBuilder{TDestination}, IResourceBuilder{IResourceWithConnectionString}, string?, bool)"/>
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource. The value of the connection string is read from the "ConnectionStrings:{resourcename}" configuration section, for example in appsettings.json or user secrets</param>
    /// <param name="environmentVariableName">Environment variable name to set when WithReference is used.</param>
    /// <returns>Resource builder for the parameter.</returns>
    /// <exception cref="DistributedApplicationException"></exception>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addConnectionString dispatcher export.")]
    public static IResourceBuilder<IResourceWithConnectionString> AddConnectionString(this IDistributedApplicationBuilder builder, [ResourceName] string name, string? environmentVariableName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.AddParameter(
                new ConnectionStringParameterResource(
                    name,
                    _ => builder.Configuration.GetConnectionString(name) ??
                        throw new MissingParameterValueException($"Connection string parameter resource could not be used because connection string '{name}' is missing."),
                    environmentVariableName)
                );
    }

    [AspireExport("addConnectionString", Description = "Adds a connection string resource")]
    internal static IResourceBuilder<IResourceWithConnectionString> AddConnectionStringForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object? environmentVariableNameOrExpression = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return environmentVariableNameOrExpression switch
        {
            null => ParameterResourceBuilderExtensions.AddConnectionString(builder, name),
            string environmentVariableName => ParameterResourceBuilderExtensions.AddConnectionString(builder, name, environmentVariableName),
            ReferenceExpression connectionStringExpression => ConnectionStringBuilderExtensions.AddConnectionString(builder, name, connectionStringExpression),
            _ => throw new ArgumentException("Connection string must be omitted, an environment variable name, or a reference expression.", nameof(environmentVariableNameOrExpression))
        };
    }

    /// <summary>
    /// Changes the resource to be published as a connection string reference in the manifest.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The configured <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Publishes the resource as a connection string")]
    public static IResourceBuilder<T> PublishAsConnectionString<T>(this IResourceBuilder<T> builder)
        where T : ContainerResource, IResourceWithConnectionString
    {
        ArgumentNullException.ThrowIfNull(builder);

        ConfigureConnectionStringManifestPublisher(builder);
        return builder;
    }

    /// <summary>
    /// Configures the manifest writer for this resource to be a parameter resource.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    public static void ConfigureConnectionStringManifestPublisher(IResourceBuilder<IResourceWithConnectionString> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Create a parameter resource that we use to write to the manifest
        var parameter = new ParameterResource(builder.Resource.Name, _ => "", secret: true);
        parameter.IsConnectionString = true;

        builder.WithManifestPublishingCallback(context => context.WriteParameterAsync(parameter));
    }

    /// <summary>
    /// Creates a default password parameter that generates a random password.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="lower"><see langword="true" /> if lowercase alphabet characters should be included; otherwise, <see langword="false" />.</param>
    /// <param name="upper"><see langword="true" /> if uppercase alphabet characters should be included; otherwise, <see langword="false" />.</param>
    /// <param name="numeric"><see langword="true" /> if numeric characters should be included; otherwise, <see langword="false" />.</param>
    /// <param name="special"><see langword="true" /> if special characters should be included; otherwise, <see langword="false" />.</param>
    /// <param name="minLower">The minimum number of lowercase characters in the result.</param>
    /// <param name="minUpper">The minimum number of uppercase characters in the result.</param>
    /// <param name="minNumeric">The minimum number of numeric characters in the result.</param>
    /// <param name="minSpecial">The minimum number of special characters in the result.</param>
    /// <returns>The created <see cref="ParameterResource"/>.</returns>
    /// <remarks>
    /// To ensure the generated password has enough entropy, see the remarks in <see cref="GenerateParameterDefault"/>.<br/>
    /// The value will be saved to the app host project's user secrets store when <see cref="DistributedApplicationExecutionContext.IsRunMode"/> is <c>true</c>.
    /// </remarks>
    public static ParameterResource CreateDefaultPasswordParameter(
        IDistributedApplicationBuilder builder, string name,
        bool lower = true, bool upper = true, bool numeric = true, bool special = true,
        int minLower = 0, int minUpper = 0, int minNumeric = 0, int minSpecial = 0)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var generatedPassword = new GenerateParameterDefault
        {
            MinLength = 22, // enough to give 128 bits of entropy when using the default 67 possible characters. See remarks in GenerateParameterDefault
            Lower = lower,
            Upper = upper,
            Numeric = numeric,
            Special = special,
            MinLower = minLower,
            MinUpper = minUpper,
            MinNumeric = minNumeric,
            MinSpecial = minSpecial
        };

        return CreateGeneratedParameter(builder, name, secret: true, generatedPassword);
    }

    /// <summary>
    /// Creates a new <see cref="ParameterResource"/> that has a generated value using the <paramref name="parameterDefault"/>.
    /// </summary>
    /// <remarks>
    /// The value will be saved to the app host project's user secrets store when <see cref="DistributedApplicationExecutionContext.IsRunMode"/> is <c>true</c>.
    /// </remarks>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="secret">Flag indicating whether the parameter should be regarded as secret.</param>
    /// <param name="parameterDefault">The <see cref="GenerateParameterDefault"/> that describes how the parameter's value should be generated.</param>
    /// <returns>The created <see cref="ParameterResource"/>.</returns>
    public static ParameterResource CreateGeneratedParameter(
        IDistributedApplicationBuilder builder, string name, bool secret, GenerateParameterDefault parameterDefault)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var parameterResource = new ParameterResource(name, defaultValue => GetParameterValue(builder.Configuration, name, defaultValue), secret)
        {
            Default = parameterDefault
        };

        if (builder.ExecutionContext.IsRunMode)
        {
            parameterResource.Default = new UserSecretsParameterDefault(builder.Environment.ApplicationName, name, parameterResource.Default, builder.UserSecretsManager);
        }

        return parameterResource;
    }

    /// <summary>
    /// Creates a new <see cref="ParameterResource"/>.
    /// </summary>
    /// <remarks>
    /// The value will be saved to the app host project's user secrets store when <see cref="DistributedApplicationExecutionContext.IsRunMode"/> is <c>true</c>.
    /// </remarks>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">Name of parameter resource</param>
    /// <param name="secret">Flag indicating whether the parameter should be regarded as secret.</param>
    /// <returns>The created <see cref="ParameterResource"/>.</returns>
    public static ParameterResource CreateParameter(IDistributedApplicationBuilder builder, string name, bool secret)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var parameterResource = new ParameterResource(name, defaultValue => GetParameterValue(builder.Configuration, name, defaultValue), secret);

        return parameterResource;
    }
}
