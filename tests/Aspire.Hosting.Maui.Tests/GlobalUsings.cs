// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Aspire.Hosting.TestUtilities already compiles the shared helper into the global namespace.
// Alias it here instead of compiling tests/Shared/TempDirectory.cs again, which would create
// duplicate TestTempDirectory definitions when this project references the helper assembly.
global using SharedTestTempDirectory = global::TestTempDirectory;
