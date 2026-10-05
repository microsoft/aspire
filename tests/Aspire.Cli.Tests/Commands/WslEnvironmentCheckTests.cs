// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Commands;

public class WslEnvironmentCheckTests
{
    // Real banner from a WSL 1 distribution. WSL 1 has no kernel of its own and always reports this
    // fixed 4.4.0 compatibility version, which is why an ordinal "major >= 4" comparison misreads it
    // as WSL 2. See https://learn.microsoft.com/windows/wsl/compare-versions
    private const string Wsl1Banner =
        "Linux version 4.4.0-19041-Microsoft (Microsoft@Microsoft.com) (gcc version 5.4.0 (GCC) ) #488-Microsoft Mon Sep 01 13:43:00 PST 2020";

    // Historical WSL 1 compatibility banner from https://github.com/Microsoft/WSL/issues/1090.
    // This release string is synthetic and should still be treated as WSL 1, not a real Linux kernel.
    private const string HistoricalWsl1Banner =
        "Linux version 3.4.0-Microsoft (Microsoft@Microsoft.com) (gcc version 4.7 (GCC) ) #1 SMP PREEMPT Wed Dec 31 14:42:53 PST 2014";

    // Parseable custom kernel release used by .wslconfig. It carries no WSL-specific tokens, so
    // the WSL environment check must rely on the surrounding shell signals instead of the banner.
    private const string CustomKernelBanner =
        "Linux version 6.1.0-custom (builder@host) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024";

    private const string CustomTerminalPlusKernelBanner =
        "Linux version 6.1.0+ (builder@host) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024";

    private const string Wsl2Banner =
        "Linux version 5.15.90.1-microsoft-standard-WSL2 (oe-user@oe-host) (x86_64-msft-linux-gcc (GCC) 9.3.0) #1 SMP Fri Jan 27 02:56:13 UTC 2023";

    private const string EarlyWsl2Banner =
        "Linux version 4.19.84-microsoft-standard (oe-user@oe-host) (gcc version 8.3.0 (GCC)) #1 SMP Wed Nov 13 11:44:37 UTC 2019";

    // Some Microsoft-shipped 4.19 WSL 2 kernels used the legacy "...-microsoft-WSL2-standard"
    // release shape before Microsoft standardized on "...-microsoft-standard[-WSL2]".
    private const string LegacyWsl2Banner =
        "Linux version 4.19.121-microsoft-WSL2-standard (oe-user@oe-host) (gcc version 8.3.0 (GCC)) #1 SMP Fri Jul 10 23:59:06 UTC 2020";

    private const string NewerWsl2Banner =
        "Linux version 6.6.87.2-microsoft-standard-WSL2 (root@builder) (gcc (GCC) 11.2.0) #1 SMP PREEMPT_DYNAMIC Thu Jun 5 18:30:46 UTC 2025";

    private const string MixedCaseWsl2Banner =
        "Linux version 5.15.90.1-MICROSOFT-STANDARD-wsl2 (oe-user@oe-host) (x86_64-msft-linux-gcc (GCC) 9.3.0) #1 SMP Fri Jan 27 02:56:13 UTC 2023";

    private const string MixedCaseEarlyWsl2Banner =
        "Linux version 4.19.84-Microsoft-Standard (oe-user@oe-host) (gcc version 8.3.0 (GCC)) #1 SMP Wed Nov 13 11:44:37 UTC 2019";

    private const string CustomWsl2LookingBanner =
        "Linux version 6.1.0-custom-WSL2 (builder@host) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024";

    private const string CustomMicrosoftStandardLookingBanner =
        "Linux version 6.1.0-microsoft-standard-custom (builder@host) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024";

    private const string CustomPrefixWsl2LookingBanner =
        "Linux version 6.1.0-custom-microsoft-standard-WSL2 (builder@host) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024";

    private const string CustomPrefixMicrosoftStandardLookingBanner =
        "Linux version 6.1.0-custom-microsoft-standard (builder@host) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024";

    private const string NativeLinuxBanner =
        "Linux version 6.8.0-64-generic (buildd@lcy02-amd64-029) (x86_64-linux-gnu-gcc-13 (Ubuntu 13.3.0-6ubuntu2~24.04) 13.3.0) #67-Ubuntu SMP PREEMPT_DYNAMIC Sun Jun 15 20:23:31 UTC 2025";

    // A native kernel whose build identity happens to carry a marker this check looks for. /proc/version
    // records who built the kernel and with what, and none of that says anything about WSL.
    private const string NativeLinuxBannerBuiltByMicrosoft =
        "Linux version 6.8.0-64-generic (Microsoft@builder) (x86_64-linux-gnu-gcc-13 (Ubuntu 13.3.0-6ubuntu2~24.04) 13.3.0) #67-Ubuntu SMP PREEMPT_DYNAMIC Sun Jun 15 20:23:31 UTC 2025";

    // A malformed nonblank /proc/version line that cannot yield a kernel release.
    private const string MalformedProcVersionBanner = "Linux version";

    // A malformed release token that contains a Microsoft marker but no numeric kernel version.
    private const string MalformedKernelReleaseBanner =
        "Linux version unknown-microsoft-standard-WSL2 (builder@host) (gcc version 11.2.0) #1 SMP";

    public static TheoryData<string?, string?, string> Wsl2PassCases => new()
    {
        { null, null, Wsl2Banner },
        { "", null, Wsl2Banner },
        { "   ", null, Wsl2Banner },
        // Once WSL is established by the shell, a parseable non-WSL1 kernel is WSL2.
        { "Ubuntu-22.04", null, NativeLinuxBanner },
        { null, "/run/WSL/interop", NativeLinuxBanner },
        { "Ubuntu-22.04", null, Wsl2Banner },
        { null, "/run/WSL/interop", Wsl2Banner },
    };

    public static TheoryData<string?, string?, string> Wsl1EnvironmentSignalCases => new()
    {
        { "Ubuntu-22.04", null, Wsl1Banner },
        { null, "/run/WSL/interop", Wsl1Banner },
        { "Ubuntu-22.04", "/run/WSL/interop", Wsl1Banner },
        { "Ubuntu-22.04", null, HistoricalWsl1Banner },
        { null, "/run/WSL/interop", HistoricalWsl1Banner },
        { "Ubuntu-22.04", "/run/WSL/interop", HistoricalWsl1Banner },
    };

    public static TheoryData<string?, string?, string?> WslUnreadableBannerSignalCases => new()
    {
        { "Ubuntu-22.04", null, null },
        { "Ubuntu-22.04", null, "   " },
        { "Ubuntu-22.04", null, MalformedProcVersionBanner },
        { "Ubuntu-22.04", null, MalformedKernelReleaseBanner },
        { null, "/run/WSL/interop", null },
        { null, "/run/WSL/interop", "   " },
        { null, "/run/WSL/interop", MalformedProcVersionBanner },
        { null, "/run/WSL/interop", MalformedKernelReleaseBanner },
    };

    [Fact]
    public void DetermineWslVersion_ReportsWsl1_ForRealWsl1Banner()
    {
        // Regression test: WSL 1 reports kernel 4.4.0, so classifying by "major version >= 4"
        // reported every real WSL 1 system as WSL 2 and suppressed the limited-container warning.
        Assert.Equal(WslVersion.Wsl1, WslEnvironmentCheck.DetermineWslVersion(Wsl1Banner));
    }

    [Fact]
    public void DetermineWslVersion_ReportsWsl1_ForHistoricalWsl1Banner()
    {
        Assert.Equal(WslVersion.Wsl1, WslEnvironmentCheck.DetermineWslVersion(HistoricalWsl1Banner));
    }

    [Theory]
    [InlineData("Linux version 3.4.0-Microsoft-custom (builder@host) (gcc version 4.7) #1 SMP")]
    [InlineData("Linux version 3.4.0-custom-Microsoft (builder@host) (gcc version 4.7) #1 SMP")]
    public void DetermineWslVersion_ReportsUnknown_ForUnofficialHistoricalWsl1Suffixes(string procVersion)
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Fact]
    public void DetermineWslVersion_ReportsWsl2_ForRealWsl2Banner()
    {
        Assert.Equal(WslVersion.Wsl2, WslEnvironmentCheck.DetermineWslVersion(Wsl2Banner));
    }

    [Fact]
    public void DetermineWslVersion_ReportsWsl2_ForEarlyMicrosoftStandardKernel()
    {
        Assert.Equal(WslVersion.Wsl2, WslEnvironmentCheck.DetermineWslVersion(EarlyWsl2Banner));
    }

    [Fact]
    public void DetermineWslVersion_ReportsWsl2_ForLegacyMicrosoftWsl2StandardKernel()
    {
        Assert.Equal(WslVersion.Wsl2, WslEnvironmentCheck.DetermineWslVersion(LegacyWsl2Banner));
    }

    [Fact]
    public void DetermineWslVersion_ReportsWsl2_ForNewerWsl2Kernel()
    {
        Assert.Equal(WslVersion.Wsl2, WslEnvironmentCheck.DetermineWslVersion(NewerWsl2Banner));
    }

    [Theory]
    // Official suffix matching is case-insensitive because the kernel release casing is not stable.
    [InlineData(MixedCaseEarlyWsl2Banner)]
    [InlineData(MixedCaseWsl2Banner)]
    public void DetermineWslVersion_ReportsWsl2_ForOfficialKernelReleaseSuffixes_WithDifferentCasing(string procVersion)
    {
        Assert.Equal(WslVersion.Wsl2, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Fact]
    public void DetermineWslVersion_ReportsUnknown_WhenBannerIsUnavailable()
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(null));
    }

    [Fact]
    public void DetermineWslVersion_ReportsUnknown_WhenBannerIsBlank()
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion("   "));
    }

    [Fact]
    public void DetermineWslVersion_ReportsUnknown_ForCustomKernelWithoutMarkers()
    {
        // A custom kernel configured through .wslconfig carries neither the WSL2 marker nor the
        // WSL 1 compatibility banner, so neither version can be claimed.
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(CustomKernelBanner));
    }

    [Fact]
    public void DetermineWslVersion_ReportsUnknown_WhenMicrosoftBannerHasNoRecognizableVersion()
    {
        // Regression test for the opposite failure: an unparseable Microsoft banner used to fall
        // through to WSL 1 and tell the user to upgrade to WSL 2 they may already be running.
        Assert.Equal(
            WslVersion.Unknown,
            WslEnvironmentCheck.DetermineWslVersion("Linux version unknown-microsoft (build@host)"));
    }

    [Theory]
    // A 4.4.x kernel that is not the fixed WSL 1 compatibility release, built by someone whose name
    // happens to contain "Microsoft". Matching only the "4.4." prefix plus a "Microsoft" mention
    // anywhere in the banner reported this as WSL 1 and recommended an upgrade that does not apply.
    [InlineData("Linux version 4.4.1-custom (Microsoft@builder) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024")]
    // The WSL 1 build number is required: a 4.4.0 release without it is a stock kernel, not WSL 1.
    [InlineData("Linux version 4.4.0-generic (Microsoft@builder) #1 SMP Tue Jan 2 00:00:00 UTC 2024")]
    public void DetermineWslVersion_ReportsUnknown_ForNonWsl1KernelsMentioningMicrosoft(string procVersion)
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    // Real WSL 1 banners across Windows builds. The build number varies, the rest of the release does not.
    [InlineData("Linux version 4.4.0-19041-Microsoft (Microsoft@Microsoft.com) (gcc version 5.4.0 (GCC) ) #488-Microsoft Mon Sep 01 13:43:00 PST 2020")]
    [InlineData("Linux version 4.4.0-18362-Microsoft (Microsoft@Microsoft.com) (gcc version 5.4.0 (GCC) ) #476-Microsoft Fri Nov 01 16:53:00 PST 2019")]
    [InlineData("Linux version 4.4.0-43-Microsoft (Microsoft@Microsoft.com) (gcc version 5.4.0 (GCC) ) #1-Microsoft Wed Dec 31 14:42:53 PST 2014")]
    public void DetermineWslVersion_ReportsWsl1_AcrossWsl1BuildNumbers(string procVersion)
    {
        Assert.Equal(WslVersion.Wsl1, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    // The WSL 2 marker in the build identity rather than the kernel release. Scanning the whole banner
    // reported this custom kernel as a confident WSL 2 pass.
    [InlineData("Linux version 6.1.0-custom (wsl2@builder) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024")]
    // Same for the early WSL 2 marker: "microsoft-standard" in the build identity is not a kernel release.
    [InlineData("Linux version 6.1.0-custom (microsoft-standard@builder) (gcc version 11.2.0) #1 SMP Tue Jan 2 00:00:00 UTC 2024")]
    public void DetermineWslVersion_ReportsUnknown_WhenMarkersAppearOnlyInBuildMetadata(string procVersion)
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    // These releases only borrow official WSL 2 tokens as substrings. Matching them with Contains(...)
    // reported them as real WSL 2 kernels and suppressed the unknown-version warning.
    [InlineData(CustomWsl2LookingBanner)]
    [InlineData(CustomMicrosoftStandardLookingBanner)]
    public void DetermineWslVersion_ReportsUnknown_ForUnofficialKernelReleaseSuffixes(string procVersion)
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    // These kernels end with the official tokens but insert extra release segments before them.
    // Accepting them would treat a custom kernel name as if it were an observed Microsoft release.
    [InlineData(CustomPrefixWsl2LookingBanner)]
    [InlineData(CustomPrefixMicrosoftStandardLookingBanner)]
    public void DetermineWslVersion_ReportsUnknown_ForUnofficialKernelReleasePrefixes(string procVersion)
    {
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Fact]
    public void CreateResult_MakesTheUpgradeConditional_WhenVersionIsUnknown()
    {
        // A missing or malformed banner can classify an established WSL 2 environment as Unknown,
        // so unconditional advice could tell that user to upgrade an environment already on WSL 2.
        Assert.Equal(
            "Run 'wsl --list --verbose' from Windows to check the version. If it reports 1, upgrade with: wsl --set-version <distro> 2",
            WslEnvironmentCheck.CreateResult(WslVersion.Unknown).Fix);
    }

    [Fact]
    public void CreateResult_ReportsWarning_WhenVersionIsUnknown()
    {
        // The core three-state guarantee: an undetermined version must never be reported as healthy.
        var result = WslEnvironmentCheck.CreateResult(WslVersion.Unknown);

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("WSL detected but the version could not be determined", result.Message);
        Assert.Contains("/proc/version", result.Details);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public void CreateResult_ReportsWarning_ForWsl1()
    {
        var result = WslEnvironmentCheck.CreateResult(WslVersion.Wsl1);

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("WSL1 detected - limited container support", result.Message);
    }

    [Fact]
    public void CreateResult_ReportsPass_ForWsl2()
    {
        var result = WslEnvironmentCheck.CreateResult(WslVersion.Wsl2);

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
    }

    [Fact]
    public async Task CheckAsync_ReportsNothing_WhenNotLinux()
    {
        var check = new WslEnvironmentCheck(TestEnvironment.CreateMacOS(), () => Wsl1Banner);

        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_ReportsNothing_WhenLinuxIsNotWsl(string? distroName)
    {
        // Blank WSL_DISTRO_NAME values should behave like the signal is absent.
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment(distroName),
            () => NativeLinuxBanner);

        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckAsync_ReportsNothing_WhenWslInteropIsBlank(string wslInterop)
    {
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment(null, wslInterop),
            () => NativeLinuxBanner);

        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(NativeLinuxBannerBuiltByMicrosoft)]
    [InlineData("Linux version 6.8.0-64-generic (wsl2@builder) (gcc version 13.3.0) #67 SMP PREEMPT_DYNAMIC Sun Jun 15 20:23:31 UTC 2025")]
    public async Task CheckAsync_ReportsNothing_WhenOnlyBuildMetadataMentionsWsl(string procVersion)
    {
        // Detecting WSL from anywhere in the banner turned a native kernel into a "WSL detected but the
        // version could not be determined" warning on a machine that has nothing to do with WSL.
        var check = new WslEnvironmentCheck(TestEnvironment.CreateLinux(), () => procVersion);

        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CustomWsl2LookingBanner)]
    [InlineData(CustomMicrosoftStandardLookingBanner)]
    public async Task CheckAsync_ReportsNothing_ForUnofficialKernelReleaseSuffixes_WhenWslDistroNameIsNotSet(string procVersion)
    {
        var check = new WslEnvironmentCheck(TestEnvironment.CreateLinux(), () => procVersion);

        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CustomPrefixWsl2LookingBanner)]
    [InlineData(CustomPrefixMicrosoftStandardLookingBanner)]
    public async Task CheckAsync_ReportsNothing_ForUnofficialKernelReleasePrefixes_WhenWslDistroNameIsNotSet(string procVersion)
    {
        var check = new WslEnvironmentCheck(TestEnvironment.CreateLinux(), () => procVersion);

        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Wsl2PassCases))]
    public async Task CheckAsync_ReportsPass_WhenWsl2IsDetected(
        string? distroName,
        string? wslInterop,
        string procVersion)
    {
        var check = new WslEnvironmentCheck(CreateLinuxEnvironment(distroName, wslInterop), () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
    }

    [Fact]
    public async Task CheckAsync_ReportsWarning_ForWsl1()
    {
        var check = new WslEnvironmentCheck(TestEnvironment.CreateLinux(), () => Wsl1Banner);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("WSL1 detected - limited container support", result.Message);
    }

    [Fact]
    public async Task CheckAsync_ReportsWarning_ForHistoricalWsl1Banner()
    {
        var check = new WslEnvironmentCheck(TestEnvironment.CreateLinux(), () => HistoricalWsl1Banner);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("WSL1 detected - limited container support", result.Message);
    }

    [Theory]
    [MemberData(nameof(Wsl1EnvironmentSignalCases))]
    public async Task CheckAsync_ReportsWarning_ForWsl1Banners_WhenWslDistroNameOrWslInteropIsSet(
        string? distroName,
        string? wslInterop,
        string procVersion)
    {
        // WSL_DISTRO_NAME and WSL_INTEROP establish that the shell is already inside WSL. Those
        // environment signals should not hide a WSL1 banner behind the generic unknown warning.
        var check = new WslEnvironmentCheck(CreateLinuxEnvironment(distroName, wslInterop), () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("WSL1 detected - limited container support", result.Message);
    }

    [Theory]
    [InlineData(CustomWsl2LookingBanner)]
    [InlineData(CustomMicrosoftStandardLookingBanner)]
    public async Task CheckAsync_ReportsPass_ForUnofficialKernelReleaseSuffixes_WhenWslDistroNameIsSet(string procVersion)
    {
        // WSL_DISTRO_NAME means we're already inside WSL. A parseable custom kernel should be treated
        // as WSL2 instead of being downgraded to "unknown".
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment("Ubuntu-22.04"),
            () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    [InlineData(CustomPrefixWsl2LookingBanner)]
    [InlineData(CustomPrefixMicrosoftStandardLookingBanner)]
    public async Task CheckAsync_ReportsPass_ForUnofficialKernelReleasePrefixes_WhenWslDistroNameIsSet(string procVersion)
    {
        // WSL_DISTRO_NAME means we're already inside WSL. A parseable custom kernel should be treated
        // as WSL2 instead of being downgraded to "unknown".
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment("Ubuntu-22.04"),
            () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    [InlineData(CustomKernelBanner)]
    [InlineData(CustomTerminalPlusKernelBanner)]
    public async Task CheckAsync_ReportsPass_ForCustomKernelWithoutMarkers_WhenWslDistroNameIsSet(string procVersion)
    {
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment("Ubuntu-22.04"),
            () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
    }

    [Theory]
    [InlineData(CustomWsl2LookingBanner)]
    [InlineData(CustomMicrosoftStandardLookingBanner)]
    public async Task CheckAsync_ReportsPass_ForUnofficialKernelReleaseSuffixes_WhenWslInteropIsSet(string procVersion)
    {
        // WSL_INTEROP is injected into WSL shells even when WSL_DISTRO_NAME is absent. Once WSL is
        // established, a parseable custom release should still be treated as WSL2.
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment(null, "/run/WSL/interop"),
            () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    [InlineData(CustomPrefixWsl2LookingBanner)]
    [InlineData(CustomPrefixMicrosoftStandardLookingBanner)]
    public async Task CheckAsync_ReportsPass_ForUnofficialKernelReleasePrefixes_WhenWslInteropIsSet(string procVersion)
    {
        // WSL_INTEROP is injected into WSL shells even when WSL_DISTRO_NAME is absent. Once WSL is
        // established, a parseable custom release should still be treated as WSL2.
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment(null, "/run/WSL/interop"),
            () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    [InlineData(CustomKernelBanner)]
    [InlineData(CustomTerminalPlusKernelBanner)]
    public async Task CheckAsync_ReportsPass_ForCustomKernelWithoutMarkers_WhenWslInteropIsSet(string procVersion)
    {
        var check = new WslEnvironmentCheck(
            CreateLinuxEnvironment(null, "/run/WSL/interop"),
            () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
    }

    [Theory]
    [MemberData(nameof(WslUnreadableBannerSignalCases))]
    public async Task CheckAsync_ReportsWarning_WhenBannerIsMissingOrUnreadableAndWslIsDetected(
        string? distroName,
        string? wslInterop,
        string? procVersion)
    {
        // WSL_DISTRO_NAME and WSL_INTEROP both mean the shell is already inside WSL, so an unreadable
        // banner must warn instead of looking like the machine is not WSL at all.
        var check = new WslEnvironmentCheck(CreateLinuxEnvironment(distroName, wslInterop), () => procVersion);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("WSL detected but the version could not be determined", result.Message);
        Assert.Equal(WslVersion.Unknown, WslEnvironmentCheck.DetermineWslVersion(procVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ubuntu-22.04")]
    public async Task CheckAsync_ReportsPass_ForLegacyMicrosoftWsl2StandardKernel(string? distroName)
    {
        var check = new WslEnvironmentCheck(CreateLinuxEnvironment(distroName), () => LegacyWsl2Banner);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("WSL2 environment detected", result.Message);
    }

    private static TestEnvironment CreateLinuxEnvironment(string? distroName, string? wslInterop = null)
    {
        if (distroName is null && wslInterop is null)
        {
            return TestEnvironment.CreateLinux();
        }

        var environmentVariables = new Dictionary<string, string?>();

        if (distroName is not null)
        {
            environmentVariables["WSL_DISTRO_NAME"] = distroName;
        }

        if (wslInterop is not null)
        {
            environmentVariables["WSL_INTEROP"] = wslInterop;
        }

        return TestEnvironment.CreateLinux(environmentVariables);
    }
}
