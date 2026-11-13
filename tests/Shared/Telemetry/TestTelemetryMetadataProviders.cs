// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Shared.Telemetry;

namespace Aspire.Tests.Shared.Telemetry;

[SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "The CLI fixture derives from this shared fake.")]
internal class TestMachineInformationProvider : IMachineInformationProvider
{
    public string? DeviceId { get; set; } = "test-device-id";
    public string MacAddressHash { get; set; } = "test-mac-hash";
    public string UserName { get; set; } = string.Empty;
    public string UserDomainName { get; set; } = string.Empty;
    public Func<Task<string?>>? GetDeviceIdCallback { get; set; }
    public Func<Task<string>>? GetMacAddressHashCallback { get; set; }

    public Task<string?> GetOrCreateDeviceId() => GetDeviceIdCallback?.Invoke() ?? Task.FromResult(DeviceId);
    public Task<string> GetMacAddressHash() => GetMacAddressHashCallback?.Invoke() ?? Task.FromResult(MacAddressHash);
}

[SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "The CLI fixture derives from this shared fake.")]
internal class TestInternalMicrosoftDetector : IInternalMicrosoftDetector
{
    private int _invocationCount;

    public bool IsInternalMicrosoft { get; set; }
    public string? Source { get; set; }
    public string? Alias { get; set; }
    public string? Domain { get; set; }
    public IReadOnlyList<InternalMicrosoftProbeDiagnostic> ProbeDiagnostics { get; set; } = [];
    public Exception? ExceptionToThrow { get; set; }
    public Func<CancellationToken, Task<InternalMicrosoftDetectionResult>>? DetectionCallback { get; set; }
    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public Task<InternalMicrosoftDetectionResult> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);
        if (ExceptionToThrow is not null)
        {
            return Task.FromException<InternalMicrosoftDetectionResult>(ExceptionToThrow);
        }
        if (DetectionCallback is not null)
        {
            return DetectionCallback(cancellationToken);
        }

        return Task.FromResult(new InternalMicrosoftDetectionResult(
            IsInternalMicrosoft, Source, Alias, Domain,
            IsInternalMicrosoft ? InternalMicrosoftDetectorOutcome.Detected : InternalMicrosoftDetectorOutcome.NotDetected,
            InternalMicrosoftDetectorCacheStatus.Miss, TimeSpan.FromMilliseconds(1), ProbeDiagnostics));
    }
}
