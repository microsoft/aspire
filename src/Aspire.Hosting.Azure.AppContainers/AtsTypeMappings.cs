// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Azure.Provisioning.AppContainers;

// ============================================================================
// ATS Type Exports for Aspire.Hosting.Azure.AppContainers
// ============================================================================
// These assembly-level attributes mark external types as ATS exports so they
// can be used as callback context parameters in polyglot app hosts.

[assembly: AspireExport(typeof(ContainerApp), ExposeProperties = true)]
[assembly: AspireExport(typeof(ContainerAppJob), ExposeProperties = true)]
