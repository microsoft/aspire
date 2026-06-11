// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Exception thrown when a capability invocation fails.
/// Contains structured error information for ATS error responses.
/// </summary>
internal sealed class CapabilityException : Exception
{
    /// <summary>
    /// Creates a new CapabilityException.
    /// </summary>
    /// <param name="error">The ATS error.</param>
    public CapabilityException(AtsError error)
        : base(error.Message)
    {
        Error = error;
    }

    /// <summary>
    /// Creates a new CapabilityException with an inner exception.
    /// </summary>
    /// <param name="error">The ATS error.</param>
    /// <param name="innerException">The inner exception.</param>
    public CapabilityException(AtsError error, Exception innerException)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Gets the ATS error.
    /// </summary>
    public AtsError Error { get; }

    /// <summary>
    /// Creates a CapabilityException for a capability not found error.
    /// </summary>
    public static CapabilityException CapabilityNotFound(string capabilityId)
    {
        return new CapabilityException(new AtsError
        {
            Code = AtsErrorCodes.CapabilityNotFound,
            Message = $"Unknown capability: {capabilityId}",
            Capability = capabilityId
        });
    }

    /// <summary>
    /// Creates a CapabilityException for a handle not found error.
    /// </summary>
    public static CapabilityException HandleNotFound(string handleId, string? capabilityId = null)
    {
        return new CapabilityException(new AtsError
        {
            Code = AtsErrorCodes.HandleNotFound,
            Message = $"Handle not found: {handleId}",
            Capability = capabilityId
        });
    }

    /// <summary>
    /// Creates a CapabilityException for a type mismatch error.
    /// </summary>
    public static CapabilityException TypeMismatch(
        string capabilityId,
        string parameterName,
        string expectedType,
        string actualType)
    {
        return new CapabilityException(new AtsError
        {
            Code = AtsErrorCodes.TypeMismatch,
            Message = $"Capability '{capabilityId}' requires handle of type '{expectedType}', got '{actualType}'",
            Capability = capabilityId,
            Details = new AtsErrorDetails
            {
                Parameter = parameterName,
                Expected = expectedType,
                Actual = actualType
            }
        });
    }

    /// <summary>
    /// Creates a CapabilityException for an invalid argument error.
    /// </summary>
    public static CapabilityException InvalidArgument(
        string capabilityId,
        string parameterName,
        string message)
    {
        return new CapabilityException(new AtsError
        {
            Code = AtsErrorCodes.InvalidArgument,
            Message = message,
            Capability = capabilityId,
            Details = new AtsErrorDetails
            {
                Parameter = parameterName
            }
        });
    }

    /// <summary>
    /// Creates a CapabilityException for a callback error.
    /// </summary>
    public static CapabilityException CallbackError(
        string capabilityId,
        string message,
        Exception innerException)
    {
        return new CapabilityException(new AtsError
        {
            Code = AtsErrorCodes.CallbackError,
            Message = message,
            Capability = capabilityId
        }, innerException);
    }

    /// <summary>
    /// Creates a CapabilityException for an internal error.
    /// </summary>
    public static CapabilityException InternalError(
        string capabilityId,
        string message,
        Exception? innerException = null)
    {
        var error = new AtsError
        {
            Code = AtsErrorCodes.InternalError,
            Message = message,
            Capability = capabilityId
        };

        return innerException != null
            ? new CapabilityException(error, innerException)
            : new CapabilityException(error);
    }
}
