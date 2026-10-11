// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Tests.Utils;

public class DcpKubeconfigDataTests
{
    [Fact]
    public void ReadsGeneratedDcpScalarsWithoutInterpretingColonsInValues()
    {
        var data = DcpKubeconfigData.Parse("""
            # server: ignored
            clusters:
            - cluster:
                server: "https://127.0.0.1:55000/"
                certificate-authority-data: 'base64-ca'
            users:
            - user:
                token: token:with:colons
                client-certificate-data: base64-cert
                client-key-data: base64-key
            """);
        Assert.Equal(new DcpKubeconfigData("https://127.0.0.1:55000/", "token:with:colons", "base64-ca", "base64-cert", "base64-key"), data);
    }

    [Fact]
    public void ReportsMissingOrPartiallyWrittenConnectionMaterial()
    {
        var data = DcpKubeconfigData.Parse("server:\n  certificate-authority-data: \n# token: ignored");
        Assert.Equal(new DcpKubeconfigData(null, null, null, null, null), data);
    }
}
