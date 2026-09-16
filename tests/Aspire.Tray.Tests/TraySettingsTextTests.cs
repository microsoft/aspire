// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests;

public class TraySettingsTextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvailableStartupDoesNotShowAnOptionalMessage(bool enabled)
    {
        Assert.Equal("", TraySettingsText.GetStartupStatus(new(enabled, true, "Installation details."), null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableStartupShowsOnlyTheShortSignedBinariesNote(bool enabled)
    {
        Assert.Equal("Launch at sign-in is only available for signed binaries.",
            TraySettingsText.GetStartupStatus(new(enabled, false, "Long installation and operating system details."), null));
    }

    [Theory]
    [InlineData(false, "off")]
    [InlineData(true, "on")]
    public void WriteErrorsRetainTheActualRegistrationState(bool enabled, string state)
    {
        var error = TraySettingsText.GetWriteError(new IOException("Access denied."));

        Assert.Equal($"Could not change launch at sign-in: Access denied.{Environment.NewLine}Launch at sign-in is {state}.",
            TraySettingsText.GetStartupStatus(new(enabled, true, null), error));
    }

    [Fact]
    public void ReadErrorsPreserveWriteFailureAndRetryInstructions()
    {
        var error = TraySettingsText.GetReadError(new IOException("Read failed."), "Write failed.");

        Assert.Equal(string.Join(Environment.NewLine,
            "Write failed.", "Could not read launch at sign-in: Read failed.", "Close and reopen Settings to retry."), error);
    }
}
