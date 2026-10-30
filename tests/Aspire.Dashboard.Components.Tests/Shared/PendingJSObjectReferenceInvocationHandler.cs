// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Bunit;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Tests.Shared;

// SetupModule completes immediately; lifecycle tests need to release object creation after disposal starts.
internal sealed class PendingJSObjectReferenceInvocationHandler(InvocationMatcher matcher)
    : JSRuntimeInvocationHandler<IJSObjectReference?>(matcher, isCatchAllHandler: false);
