// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik.Tests;

/// <summary>
/// A compute environment that isn't Docker Compose, such as Kubernetes or Azure Container Apps, for tests of a model
/// that deploys to several environments.
/// </summary>
internal sealed class TestComputeEnvironmentResource(string name) : Resource(name), IComputeEnvironmentResource;
