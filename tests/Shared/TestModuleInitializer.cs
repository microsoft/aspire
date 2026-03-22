// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.CompilerServices;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Azure.Tests;

sealed class TestModuleInitializer
{
    [ModuleInitializer]
    internal static void Setup()
    {
        // This file is compiled into multiple test assemblies. When test assemblies reference
        // each other (e.g., Aspire.Cli.Tests references Aspire.Hosting.Tests), multiple module
        // initializers may attempt to configure Verify. DerivePathInfo can only be called once
        // before any Verify test runs. We use VerifyInitializer (in the shared Aspire.TestUtilities
        // assembly) to ensure only the first caller configures it.
        if (!VerifyInitializer.TryInitialize())
        {
            return;
        }

        DerivePathInfo(
                (sourceFile, projectDirectory, type, method) => new(
                    directory: Path.Combine(
                        PlatformDetection.IsRunningOnHelix
                            ? Path.GetDirectoryName(Assembly.GetExecutingAssembly()!.Location) ?? string.Empty
                            : projectDirectory,
                        "Snapshots"),
                    typeName: type.Name,
                    methodName: method.Name));

        // auto-accept baseline changes when running locally
        VerifierSettings.AutoVerify(includeBuildServer: false, throwException: true);
    }
}
