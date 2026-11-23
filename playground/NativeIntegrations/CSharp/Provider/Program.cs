// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NativeIntegration.Generated;

using var configuration = new ConfigurationManager();
configuration.AddEnvironmentVariables();
var host = new NativeProviderHost(configuration, TimeProvider.System, NullLogger.Instance);
await host.RunAsync(ProviderContract.Create(), CancellationToken.None);
