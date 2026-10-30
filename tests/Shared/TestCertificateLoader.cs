// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;

namespace Microsoft.AspNetCore.InternalTesting;

public static class TestCertificateLoader
{
    private static readonly string s_baseDir = Path.Combine(Directory.GetCurrentDirectory(), "shared", "TestCertificates");

    private static readonly TimeSpan s_mutexTimeout = TimeSpan.FromSeconds(120);
    private static readonly Mutex? s_importPfxMutex = OperatingSystem.IsWindows()
        ? new Mutex(initiallyOwned: false, "Global\\AspireTests.Certificates.LoadPfxCertificate")
        : null;

    public static string TestCertificatePath { get; } = Path.Combine(s_baseDir, "testCert.pfx");
    public static string GetCertPath(string name) => Path.Combine(s_baseDir, name);

    public static X509Certificate2 GetTestCertificate(string certName = "testCert.pfx")
    {
         return GetTestCertificate(certName, "testPassword");
    }

    public static X509Certificate2 GetTestCertificate(string certName, string password)
    {
        // On Windows, applications should not import PFX files in parallel to avoid a known system-level
        // race condition bug in native code which can cause crashes/corruption of the certificate state.
        if (s_importPfxMutex is not null && !s_importPfxMutex.WaitOne(s_mutexTimeout))
        {
            throw new InvalidOperationException("Cannot acquire the global certificate mutex.");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(GetCertPath(certName), password);
        }
        finally
        {
            s_importPfxMutex?.ReleaseMutex();
        }
    }
}
