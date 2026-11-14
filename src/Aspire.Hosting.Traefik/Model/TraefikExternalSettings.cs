// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// The external Traefik used at publish time, configured with <c>PublishAsExternal</c>.
/// </summary>
/// <remarks>
/// The entry point and resolver only end up in <c>traefik.*</c> label values, where Docker Compose interpolates
/// <c>${VAR}</c>, so they can be parameters. The network can't: it is a YAML key in the compose file, and Compose
/// doesn't interpolate keys.
/// </remarks>
internal sealed record TraefikExternalSettings(string Network, ReferenceExpression EntryPoint, ReferenceExpression? CertResolver);

/// <summary>
/// A Docker Compose config holding the dynamic configuration of a managed Traefik at publish time.
/// </summary>
/// <param name="Environment">The Docker Compose environment the Traefik service belongs to.</param>
/// <param name="Name">The name of the config in the compose file.</param>
/// <param name="Content">The YAML content, with <c>${VAR}</c> placeholders that Docker Compose interpolates.</param>
internal sealed record TraefikPublishFileConfiguration(DockerComposeEnvironmentResource Environment, string Name, string Content);
