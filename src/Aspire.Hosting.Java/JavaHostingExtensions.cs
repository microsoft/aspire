// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREEXTENSION001 // WithDebugSupport is experimental but used internally for debug support
#pragma warning disable IDE1006 // Naming Styles - match Community Toolkit naming convention

using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Java;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Java applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class JavaHostingExtensions
{
    private const string JavaToolOptions = "JAVA_TOOL_OPTIONS";
    private static readonly string DefaultMavenWrapper = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mvnw.cmd" : "mvnw";
    private static readonly string DefaultGradleWrapper = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "gradlew.bat" : "gradlew";

    /// <summary>
    /// Adds a Java application to the application model. Executes the executable Java app.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory to use for the command.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Use <see cref="WithMavenGoal"/> or <see cref="WithGradleTask"/> to run the application via a build tool,
    /// or use the overload that accepts a <c>jarPath</c> parameter to run with <c>java -jar</c>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> AddJavaApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        var resource = new JavaAppResource(name, workingDirectory);

        var rb = builder.AddResource(resource)
            .WithArgs(ctx =>
            {
                if (resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var buildTool))
                {
                    foreach (var arg in buildTool.Args)
                    {
                        ctx.Args.Add(arg);
                    }
                }
                else if (resource.JarPath is not null)
                {
                    ctx.Args.Add("-jar");
                    ctx.Args.Add(resource.JarPath);
                }
            })
            .WithOtlpExporter()
            .WithCertificateTrustConfiguration(JavaCertificateTrustCallback)
            .WithVSCodeDebugging();

        return rb;
    }

    /// <summary>
    /// Adds a Java application with a pre-existing JAR file.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory for the Java application.</param>
    /// <param name="jarPath">The path to the JAR file to execute.</param>
    /// <param name="args">Optional arguments to pass to the Java application.</param>
    [AspireExport("addJavaAppWithJar")]
    public static IResourceBuilder<JavaAppResource> AddJavaApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string workingDirectory,
        string jarPath,
        string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentException.ThrowIfNullOrEmpty(jarPath);

        var rb = builder.AddJavaApp(name, workingDirectory);
        rb.Resource.JarPath = jarPath;

        if (args is { Length: > 0 })
        {
            rb.WithArgs(args);
        }

        return rb;
    }

    /// <summary>
    /// Adds a Maven goal to be executed before the Java application starts.
    /// </summary>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="goal">The Maven goal to execute.</param>
    /// <param name="args">Additional arguments to pass to the Maven wrapper.</param>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> WithMavenGoal(
        this IResourceBuilder<JavaAppResource> builder,
        string goal,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(goal);

        var resolvedWrapper = builder.Resource.TryGetLastAnnotation<WrapperAnnotation>(out var wrapper)
            ? wrapper.WrapperPath
            : Path.GetFullPath(Path.Combine(builder.Resource.WorkingDirectory, DefaultMavenWrapper));

        builder.Resource.Annotations.Add(
            new JavaBuildToolAnnotation(resolvedWrapper, args is { Length: > 0 } ? [goal, .. args] : [goal]));

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.WithCommand(resolvedWrapper);
        }

        return builder;
    }

    /// <summary>
    /// Adds a Gradle task to be executed before the Java application starts.
    /// </summary>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="task">The Gradle task to execute (e.g., "bootRun").</param>
    /// <param name="args">Additional arguments to pass to the Gradle wrapper.</param>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> WithGradleTask(
        this IResourceBuilder<JavaAppResource> builder,
        string task,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(task);

        if (builder.Resource.JarPath is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(WithGradleTask)} cannot be used when a {nameof(JavaAppResource.JarPath)} has been specified. " +
                $"Use either {nameof(AddJavaApp)} with a {nameof(JavaAppResource.JarPath)} or {nameof(WithGradleTask)}, not both.");
        }

        var resolvedWrapper = builder.Resource.TryGetLastAnnotation<WrapperAnnotation>(out var wrapper)
            ? wrapper.WrapperPath
            : Path.GetFullPath(Path.Combine(builder.Resource.WorkingDirectory, DefaultGradleWrapper));

        builder.Resource.Annotations.Add(
            new JavaBuildToolAnnotation(resolvedWrapper, args is { Length: > 0 } ? [task, .. args] : [task]));

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            builder.WithCommand(resolvedWrapper);
        }

        return builder;
    }

    /// <summary>
    /// Adds Maven build support to the Java application.
    /// The wrapper script path defaults to <c>mvnw</c> (or <c>mvnw.cmd</c> on Windows) in the resource's working directory,
    /// unless overridden with <see cref="WithWrapperPath"/>.
    /// </summary>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="args">Arguments to pass to the Maven wrapper. If not provided, defaults to <c>clean package</c>.</param>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> WithMavenBuild(
        this IResourceBuilder<JavaAppResource> builder,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedWrapper = builder.Resource.TryGetLastAnnotation<WrapperAnnotation>(out var wrapper)
            ? wrapper.WrapperPath
            : Path.GetFullPath(Path.Combine(builder.Resource.WorkingDirectory, DefaultMavenWrapper));

        return builder.WithJavaBuildStep(
            buildResourceName: $"{builder.Resource.Name}-maven-build",
            createResource: (name, wrapperScript, workingDirectory) => new MavenBuildResource(name, wrapperScript, workingDirectory),
            wrapperPath: resolvedWrapper,
            buildArgs: args.Length > 0 ? args : ["clean", "package"]);
    }

    /// <summary>
    /// Adds Gradle build support to the Java application.
    /// The wrapper script path defaults to <c>gradlew</c> (or <c>gradlew.bat</c> on Windows) in the resource's working directory,
    /// unless overridden with <see cref="WithWrapperPath"/>.
    /// </summary>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="args">Arguments to pass to the Gradle wrapper. If not provided, defaults to <c>clean build</c>.</param>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> WithGradleBuild(
        this IResourceBuilder<JavaAppResource> builder,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedWrapper = builder.Resource.TryGetLastAnnotation<WrapperAnnotation>(out var wrapper)
            ? wrapper.WrapperPath
            : Path.GetFullPath(Path.Combine(builder.Resource.WorkingDirectory, DefaultGradleWrapper));

        return builder.WithJavaBuildStep(
            buildResourceName: $"{builder.Resource.Name}-gradle-build",
            createResource: (name, wrapperScript, workingDirectory) => new GradleBuildResource(name, wrapperScript, workingDirectory),
            wrapperPath: resolvedWrapper,
            buildArgs: args.Length > 0 ? args : ["clean", "build"]);
    }

    private static IResourceBuilder<JavaAppResource> WithJavaBuildStep<TBuildResource>(
        this IResourceBuilder<JavaAppResource> builder,
        string buildResourceName,
        Func<string, string, string, TBuildResource> createResource,
        string wrapperPath,
        string[] buildArgs) where TBuildResource : ExecutableResource
    {
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var buildResource = createResource(buildResourceName, wrapperPath, builder.Resource.WorkingDirectory);

            var buildBuilder = builder.ApplicationBuilder.AddResource(buildResource)
                .WithArgs(buildArgs)
                .WithParentRelationship(builder.Resource)
                .ExcludeFromManifest();

            builder.WaitForCompletion(buildBuilder);
        }

        return builder;
    }

    /// <summary>
    /// Configures the custom build tool wrapper script path.
    /// This is useful when the wrapper script is not in the default location or has a non-standard name.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> to configure.</param>
    /// <param name="wrapperScript">The path to the wrapper script, relative to the resource working directory or an absolute path.</param>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> WithWrapperPath(
        this IResourceBuilder<JavaAppResource> builder,
        string wrapperScript)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(wrapperScript);

        var wrapperPath = Path.GetFullPath(Path.Combine(builder.Resource.WorkingDirectory, wrapperScript));

        builder.Resource.Annotations.Add(new WrapperAnnotation(wrapperPath));

        return builder;
    }

    /// <summary>
    /// Configures the Java Virtual Machine arguments for the Java application.
    /// The arguments are set via the <c>JAVA_TOOL_OPTIONS</c> environment variable,
    /// which is recognized by the JVM regardless of how the application is launched
    /// (e.g., <c>java -jar</c>, Maven wrapper, or Gradle wrapper).
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="args">The JVM arguments.</param>
    [AspireExport]
    public static IResourceBuilder<T> WithJvmArgs<T>(
        this IResourceBuilder<T> builder,
        string[] args) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return builder;
        }

        return builder.WithEnvironment(context =>
        {
            AppendJavaToolOptions(context, args);
        });
    }

    /// <summary>
    /// Configures the OpenTelemetry Java Agent for the Java application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentPath">The path to the OpenTelemetry Java Agent jar file.</param>
    [AspireExport]
    public static IResourceBuilder<T> WithOtelAgent<T>(
        this IResourceBuilder<T> builder,
        string? agentPath = null) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithOtlpExporter();

        if (!string.IsNullOrEmpty(agentPath))
        {
            builder.WithEnvironment(context =>
            {
                AppendJavaToolOptions(context, [$"-javaagent:{agentPath}"]);
            });
        }

        return builder;
    }

    /// <summary>
    /// Merges the specified values into the <c>JAVA_TOOL_OPTIONS</c> environment variable.
    /// This ensures that all JVM arguments are passed to the Java application regardless of how it is launched.
    /// </summary>
    private static void AppendJavaToolOptions(EnvironmentCallbackContext context, string[] values)
    {
        AppendJavaToolOptions(context.EnvironmentVariables, values);
    }

    /// <summary>
    /// Merges the specified values into the <c>JAVA_TOOL_OPTIONS</c> environment variable.
    /// </summary>
    private static void AppendJavaToolOptions(Dictionary<string, object> environmentVariables, string[] values)
    {
        var value = string.Join(' ', values);

        if (environmentVariables.TryGetValue(JavaToolOptions, out var existing) &&
            existing is string existingValue &&
            !string.IsNullOrEmpty(existingValue))
        {
            environmentVariables[JavaToolOptions] = $"{existingValue} {value}";
        }
        else
        {
            environmentVariables[JavaToolOptions] = value;
        }
    }

    /// <summary>
    /// Configures a PKCS#12 trust store for the Java application via JAVA_TOOL_OPTIONS so the JVM
    /// trusts the Aspire developer certificate and any configured certificate authorities.
    /// </summary>
    private static async Task JavaCertificateTrustCallback(CertificateTrustConfigurationCallbackAnnotationContext ctx)
    {
        // Generate a random password for the trust store.
        var trustStorePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var bundlePath = ctx.CreateCustomBundle((certificates, ct) =>
        {
            var pkcs12Builder = new Pkcs12Builder();
            var safeContents = new Pkcs12SafeContents();

            // Oracle/OpenJDK trusted cert bag attribute OID — required for entries to be
            // recognized as trustedCertEntry in a PKCS#12 trust store.
            var trustAnchorOid = new Oid("2.16.840.1.113894.746875.1.1");
            var asnWriter = new AsnWriter(AsnEncodingRules.DER);
            asnWriter.WriteObjectIdentifier("2.5.29.37.0");
            var trustAnchorValue = asnWriter.Encode();

            for (var i = 0; i < certificates.Count; i++)
            {
                // Export public-only cert to avoid including private keys in the trust store.
                var publicCert = new X509Certificate2(certificates[i].Export(X509ContentType.Cert));
                var certBag = safeContents.AddCertificate(publicCert);
                certBag.Attributes.Add(
                    new CryptographicAttributeObject(
                        trustAnchorOid,
                        new AsnEncodedDataCollection(new AsnEncodedData(trustAnchorOid, trustAnchorValue))));
            }

            pkcs12Builder.AddSafeContentsUnencrypted(safeContents);
            pkcs12Builder.SealWithMac(trustStorePassword, HashAlgorithmName.SHA256, iterationCount: 2048);

            return Task.FromResult(pkcs12Builder.Encode());
        });

        // Resolve the bundle path to a string before using it in environment variables.
        // The ReferenceExpression from CreateCustomBundle must be resolved to avoid serialization issues.
        var bundlePathValue = await bundlePath.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);

        // Configure the JVM to use the generated PKCS#12 trust store via JAVA_TOOL_OPTIONS.
        // JAVA_TOOL_OPTIONS is processed by the JVM at startup, avoiding the current limitation
        // where WithJvmArgs are placed after -jar and would be treated as application args.
        // Preserve any existing JAVA_TOOL_OPTIONS value set by the user or another configuration source.
        var trustStoreArgs = $"-Djavax.net.ssl.trustStore={bundlePathValue} -Djavax.net.ssl.trustStoreType=PKCS12 -Djavax.net.ssl.trustStorePassword={trustStorePassword}";
        if (ctx.EnvironmentVariables.TryGetValue("JAVA_TOOL_OPTIONS", out var existing))
        {
            var existingValue = existing switch
            {
                ReferenceExpression re => await re.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false),
                _ => existing.ToString()
            };
            ctx.EnvironmentVariables["JAVA_TOOL_OPTIONS"] = $"{existingValue} {trustStoreArgs}";
        }
        else
        {
            ctx.EnvironmentVariables["JAVA_TOOL_OPTIONS"] = trustStoreArgs;
        }
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

#pragma warning disable ASPIREEXTENSION001
        return builder.WithDebugSupport(
            mode => new JavaLaunchConfiguration { Mode = mode, WorkingDirectory = builder.Resource.WorkingDirectory },
            "java");
#pragma warning restore ASPIREEXTENSION001
    }
}
