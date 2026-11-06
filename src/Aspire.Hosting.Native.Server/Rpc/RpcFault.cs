// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Creates classified failures without disclosing caller inputs or implementation exception messages.</summary>
internal sealed class RpcFault(string faultCode, string message) : InvalidOperationException(message)
{
    public const string ProviderTimeoutCode = "PROVIDER_TIMEOUT";
    public string FaultCode { get; } = faultCode;

    public static RpcFault InvalidArgument(string message) => new("INVALID_ARGUMENT", message);
    public static RpcFault InvalidHandle(string message) => new("INVALID_HANDLE", message);
    public static RpcFault HandleNotFound(string message) => new("HANDLE_NOT_FOUND", message);
    public static RpcFault HandleLimit(string message) => new("HANDLE_LIMIT", message);
    public static RpcFault TypeMismatch(string message) => new("TYPE_MISMATCH", message);
    public static RpcFault UnknownCapability(string message) => new("UNKNOWN_CAPABILITY", message);
    public static RpcFault CapabilityNotFound(string message) => new("CAPABILITY_NOT_FOUND", message);
    public static RpcFault Unauthenticated(string message) => new("UNAUTHENTICATED", message);
    public static RpcFault MethodNotFound(string message) => new("METHOD_NOT_FOUND", message);
    public static RpcFault NotSupported(string message) => new("NOT_SUPPORTED", message);
    public static RpcFault RpcBusy(string message) => new("RPC_BUSY", message);
    public static RpcFault RequestTooLarge(string message) => new("REQUEST_TOO_LARGE", message);
    public static RpcFault ProviderLimit(string message) => new("PROVIDER_LIMIT", message);
    public static RpcFault ProviderRegistrationRejected(string message) => new("PROVIDER_REGISTRATION_REJECTED", message);
    public static RpcFault ProviderConflict(string message) => new("PROVIDER_CONFLICT", message);
    public static RpcFault ProviderTimeout(string message) => new(ProviderTimeoutCode, message);
    public static RpcFault InvalidRequest(string message) => new("INVALID_REQUEST", message);
    public static RpcFault ProviderLifecycleCancelled(string message) => new("PROVIDER_LIFECYCLE_CANCELLED", message);
    public static RpcFault ProviderLifecycleTimeout(string message) => new("PROVIDER_LIFECYCLE_TIMEOUT", message);
    public static RpcFault ProviderLifecycleFailed(string message) => new("PROVIDER_LIFECYCLE_FAILED", message);
    public static RpcFault ProviderDisconnected(string message) => new("PROVIDER_DISCONNECTED", message);
    public static RpcFault ProviderFailed(string message) => new("PROVIDER_FAILED", message);
    public static RpcFault LifecycleAuthorityRequired(string message) => new("LIFECYCLE_AUTHORITY_REQUIRED", message);
    public static RpcFault ProviderDisposalFailed(string message) => new("PROVIDER_DISPOSAL_FAILED", message);
    public static RpcFault ProviderMetadataInvalid(string message) => new("PROVIDER_METADATA_INVALID", message);
    public static RpcFault ProviderMetadataUnsupported(string message) => new("PROVIDER_METADATA_UNSUPPORTED", message);
}
