// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Agents.Tests.TestServices;

internal sealed class McpTestResource(string name) : Resource(name), IResourceWithEndpoints, IResourceWithWaitSupport;
