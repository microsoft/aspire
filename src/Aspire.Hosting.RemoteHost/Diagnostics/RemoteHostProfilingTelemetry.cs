// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.TypeSystem;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.RemoteHost.Diagnostics;

internal sealed class RemoteHostProfilingTelemetry(IConfiguration configuration) : IDisposable
{
    public const string ActivitySourceName = "Aspire.Hosting.RemoteHost.Profiling";

    public static RemoteHostProfilingTelemetry Disabled { get; } = new(new ConfigurationBuilder().Build());

    private readonly ActivitySource _activitySource = new(ActivitySourceName);

    internal static class EnvironmentVariables
    {
        public const string Enabled = KnownConfigNames.ProfilingEnabled;
        public const string SessionId = KnownConfigNames.ProfilingSessionId;
        public const string TraceParent = KnownConfigNames.ProfilingTraceParent;
        public const string TraceState = KnownConfigNames.ProfilingTraceState;
    }

    internal static class Baggage
    {
        public const string SessionId = "aspire.profiling.session_id";
    }

    internal static class Activities
    {
        // Activity names describe remote AppHost server and RPC work. Keep names stable
        // because profiling exports are queried across CLI and AppHost versions.
        public const string RemoteHostRun = "aspire.hosting.remotehost.run";
        public const string JsonRpcListen = "aspire.hosting.remotehost.jsonrpc.listen";
        public const string JsonRpcConnection = "aspire.hosting.remotehost.jsonrpc.connection";
        public const string JsonRpcServerCall = "aspire.hosting.remotehost.jsonrpc.server";
        public const string AssemblyLoad = "aspire.hosting.remotehost.assembly.load";
        public const string AtsContextCreate = "aspire.hosting.remotehost.ats.context_create";
        public const string CapabilityScan = "aspire.hosting.remotehost.ats.capability_scan";
        public const string CapabilityInvoke = "aspire.hosting.remotehost.capability.invoke";
        public const string CodeGenerationGetCapabilities = "aspire.hosting.remotehost.codegen.get_capabilities";
        public const string CodeGenerationGenerate = "aspire.hosting.remotehost.codegen.generate";
        public const string LanguageDetect = "aspire.hosting.remotehost.language.detect";
        public const string LanguageGetRuntimeSpec = "aspire.hosting.remotehost.language.get_runtime_spec";
        public const string LanguageScaffold = "aspire.hosting.remotehost.language.scaffold";
    }

    internal static class Tags
    {
        // Tags capture low-cardinality dimensions and diagnostics for remote-host
        // profiling spans. Avoid raw paths, URLs, tokens, command lines, and raw argument
        // values; capability identity and argument shape are safe enough for profiling.
        public const string ProfilingSessionId = "aspire.profiling.session_id";
        public const string LegacyStartupOperationId = "aspire.startup.operation_id";
        public const string Transport = "aspire.hosting.remotehost.transport";
        public const string ActiveClientCount = "aspire.hosting.remotehost.jsonrpc.active_client_count";
        public const string DisconnectReason = "aspire.hosting.remotehost.jsonrpc.disconnect_reason";
        public const string JsonRpcMethod = "rpc.method";
        public const string JsonRpcStreaming = "aspire.hosting.remotehost.jsonrpc.streaming";
        public const string AuthenticationSucceeded = "aspire.hosting.remotehost.authentication.succeeded";
        public const string AssemblyCount = "aspire.hosting.remotehost.assembly.count";
        public const string AssemblyCacheHit = "aspire.hosting.remotehost.assembly.cache_hit";
        public const string AssemblyRequestedNames = "aspire.hosting.remotehost.assembly.requested_names";
        public const string AssemblyLoadedNames = "aspire.hosting.remotehost.assembly.loaded_names";
        public const string CapabilityScanFirstScan = "aspire.hosting.remotehost.capability_scan.first_scan";
        public const string CapabilityCount = "aspire.hosting.remotehost.capability.count";
        public const string HandleTypeCount = "aspire.hosting.remotehost.handle_type.count";
        public const string DtoTypeCount = "aspire.hosting.remotehost.dto_type.count";
        public const string EnumTypeCount = "aspire.hosting.remotehost.enum_type.count";
        public const string ExportedValueCount = "aspire.hosting.remotehost.exported_value.count";
        public const string DiagnosticCount = "aspire.hosting.remotehost.diagnostic.count";
        public const string CapabilityId = "aspire.hosting.remotehost.capability.id";
        public const string CapabilityPackage = "aspire.hosting.remotehost.capability.package";
        public const string CapabilityKind = "aspire.hosting.remotehost.capability.kind";
        public const string CapabilityArgumentCount = "aspire.hosting.remotehost.capability.argument.count";
        public const string CapabilityArgumentNames = "aspire.hosting.remotehost.capability.argument.names";
        public const string Language = "aspire.hosting.remotehost.language";
        public const string FileCount = "aspire.hosting.remotehost.file.count";
        public const string DetectionMatched = "aspire.hosting.remotehost.language.detection_matched";
        public const string ExceptionType = "exception.type";
        public const string ExceptionMessage = "exception.message";
    }

    internal static class Events
    {
        // Events mark meaningful points within longer remote-host spans, such as
        // socket readiness, connection lifecycle, and authentication decisions.
        public const string JsonRpcServerListening = "aspire.hosting.remotehost.jsonrpc.server_listening";
        public const string JsonRpcClientConnected = "aspire.hosting.remotehost.jsonrpc.client_connected";
        public const string JsonRpcListening = "aspire.hosting.remotehost.jsonrpc.listening";
        public const string JsonRpcConnectionClosed = "aspire.hosting.remotehost.jsonrpc.connection_closed";
        public const string AuthenticationAccepted = "aspire.hosting.remotehost.authentication.accepted";
        public const string AuthenticationRejected = "aspire.hosting.remotehost.authentication.rejected";
        public const string Exception = "exception";
    }

    internal static class Values
    {
        public const string NamedPipe = "named_pipe";
        public const string UnixDomainSocket = "unix_domain_socket";
    }

    public bool IsEnabled => IsProfilingEnabled(configuration);

    public static bool IsProfilingEnabled(IConfiguration? configuration)
    {
        return IsTruthy(configuration?[EnvironmentVariables.Enabled]) ||
            IsTruthy(configuration?[KnownConfigNames.Legacy.StartupProfilingEnabled]);
    }

    public static bool ShouldConfigureExporter(IConfiguration? configuration)
    {
        if (!IsProfilingEnabled(configuration))
        {
            return false;
        }

        return !string.IsNullOrEmpty(configuration?[KnownOtelConfigNames.ExporterOtlpEndpoint]);
    }

    public ActivityScope StartRemoteHostRun()
    {
        return StartActivity(Activities.RemoteHostRun);
    }

    public ActivityScope StartJsonRpcListen(string transport)
    {
        var activity = StartActivity(Activities.JsonRpcListen);
        activity.SetTransport(transport);
        return activity;
    }

    public ActivityScope StartJsonRpcConnection()
    {
        return StartActivity(Activities.JsonRpcConnection);
    }

    public ActivityScope StartJsonRpcServerCall(string methodName, bool streaming = false)
    {
        var activity = StartActivity(Activities.JsonRpcServerCall, ActivityKind.Server);
        activity.SetJsonRpcCall(methodName, streaming);
        return activity;
    }

    public ActivityScope StartJsonRpcInvokeCapability(string capabilityId, JsonObject? args)
    {
        var activity = StartJsonRpcServerCall("invokeCapability");
        activity.SetCapabilityInvocation(capabilityId, args);
        return activity;
    }

    public ActivityScope StartAssemblyLoad(bool cacheHit)
    {
        var activity = StartActivity(Activities.AssemblyLoad);
        activity.SetAssemblyCacheHit(cacheHit);
        return activity;
    }

    public ActivityScope StartAtsContextCreate()
    {
        return StartActivity(Activities.AtsContextCreate, preferConfiguredParent: true);
    }

    public ActivityScope StartCapabilityScan(int assemblyCount, bool firstScan)
    {
        var activity = StartActivity(Activities.CapabilityScan);
        activity.SetAssemblyCount(assemblyCount);
        activity.SetCapabilityScanFirstScan(firstScan);
        return activity;
    }

    public ActivityScope StartCapabilityInvoke(string capabilityId, AtsCapabilityInfo? capability)
    {
        var activity = StartActivity(Activities.CapabilityInvoke);
        activity.SetCapability(capabilityId, capability);
        return activity;
    }

    public ActivityScope StartCodeGenerationGetCapabilities()
    {
        return StartActivity(Activities.CodeGenerationGetCapabilities, ActivityKind.Server);
    }

    public ActivityScope StartCodeGenerationGenerate(string language)
    {
        var activity = StartActivity(Activities.CodeGenerationGenerate, ActivityKind.Server);
        activity.SetLanguage(language);
        return activity;
    }

    public ActivityScope StartLanguageDetect()
    {
        return StartActivity(Activities.LanguageDetect, ActivityKind.Server);
    }

    public ActivityScope StartLanguageGetRuntimeSpec(string language)
    {
        var activity = StartActivity(Activities.LanguageGetRuntimeSpec, ActivityKind.Server);
        activity.SetLanguage(language);
        return activity;
    }

    public ActivityScope StartLanguageScaffold(string language)
    {
        var activity = StartActivity(Activities.LanguageScaffold, ActivityKind.Server);
        activity.SetLanguage(language);
        return activity;
    }

    private ActivityScope StartActivity(
        string name,
        ActivityKind activityKind = ActivityKind.Internal,
        bool preferConfiguredParent = false)
    {
        if (!IsEnabled)
        {
            return default;
        }

        var ambientActivity = Activity.Current;
        Activity? activity;
        if (preferConfiguredParent &&
            TryGetConfiguredActivityContext(out var preferredParentContext))
        {
            activity = _activitySource.StartActivity(name, activityKind, preferredParentContext);
        }
        else if (TryGetAmbientRemoteParentContext(ambientActivity, out var ambientRemoteParent))
        {
            // StreamJsonRpc creates an unexported server activity from the caller's
            // traceparent. Parent profiling spans to the remote caller instead so
            // exported CLI and RemoteHost spans are adjacent in the trace.
            activity = _activitySource.StartActivity(name, activityKind, ambientRemoteParent);
        }
        else if ((ambientActivity is null || ambientActivity.Source.Name != ActivitySourceName) &&
            TryGetConfiguredActivityContext(out var parentContext))
        {
            activity = _activitySource.StartActivity(name, activityKind, parentContext);
        }
        else
        {
            activity = _activitySource.StartActivity(name, activityKind);
        }

        AddProfilingSession(activity, ambientActivity);
        return new ActivityScope(activity);
    }

    private static bool TryGetAmbientRemoteParentContext(Activity? ambientActivity, out ActivityContext parentContext)
    {
        if (ambientActivity is not null &&
            ambientActivity.Source.Name != ActivitySourceName &&
            ambientActivity.Parent is null &&
            ambientActivity.ParentSpanId != default)
        {
            parentContext = new ActivityContext(
                ambientActivity.TraceId,
                ambientActivity.ParentSpanId,
                ambientActivity.ActivityTraceFlags,
                ambientActivity.TraceStateString,
                isRemote: true);
            return true;
        }

        parentContext = default;
        return false;
    }

    private void AddProfilingSession(Activity? activity, Activity? ambientActivity)
    {
        if (activity is null)
        {
            return;
        }

        // Profiling spans can be siblings under StreamJsonRpc's short-lived activities.
        // Seed the ambient ancestor chain with baggage so later profiling siblings reuse
        // the same session after an intermediate parent activity has ended.
        var sessionId = GetProfilingSessionIdFromAncestors(ambientActivity) ?? GetProfilingSessionId(activity) ?? GetConfiguredSessionId() ?? Guid.NewGuid().ToString("N");
        AddProfilingSessionBaggage(ambientActivity, sessionId);

        // Keep profiling tags on profiling spans only. Non-profiling ambient activities only
        // carry the session as baggage so it can flow across async and RPC boundaries.
        activity.SetBaggage(Baggage.SessionId, sessionId);
        activity.SetTag(Tags.ProfilingSessionId, sessionId);
        activity.SetTag(Tags.LegacyStartupOperationId, sessionId);
    }

    private bool TryGetConfiguredActivityContext(out ActivityContext activityContext)
    {
        var traceParent = GetConfigurationValue(configuration, EnvironmentVariables.TraceParent, KnownConfigNames.Legacy.StartupTraceParent);
        var traceState = GetConfigurationValue(configuration, EnvironmentVariables.TraceState, KnownConfigNames.Legacy.StartupTraceState);
        if (!string.IsNullOrEmpty(traceParent) &&
            ActivityContext.TryParse(traceParent, traceState, out activityContext))
        {
            return true;
        }

        activityContext = default;
        return false;
    }

    private string? GetConfiguredSessionId()
    {
        return GetConfigurationValue(configuration, EnvironmentVariables.SessionId, KnownConfigNames.Legacy.StartupOperationId);
    }

    private static string? GetConfigurationValue(IConfiguration? configuration, string name, string legacyName)
    {
        return configuration?[name] is { Length: > 0 } value ? value : configuration?[legacyName];
    }

    private static string? GetProfilingSessionId(Activity? activity)
    {
        return activity?.GetBaggageItem(Baggage.SessionId) is { Length: > 0 } sessionId ? sessionId : null;
    }

    private static string? GetProfilingSessionIdFromAncestors(Activity? activity)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (GetProfilingSessionId(current) is { } sessionId)
            {
                return sessionId;
            }
        }

        return null;
    }

    private static void AddProfilingSessionBaggage(Activity? activity, string sessionId)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (GetProfilingSessionId(current) is null)
            {
                current.SetBaggage(Baggage.SessionId, sessionId);
            }
        }
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    public void Dispose()
    {
        _activitySource.Dispose();
    }

    internal readonly struct ActivityScope(Activity? activity) : IDisposable
    {
        public bool IsRunning => activity is not null;

        public void AddAuthenticationResult(bool authenticated)
        {
            SetTag(Tags.AuthenticationSucceeded, authenticated);
            AddEvent(authenticated ? Events.AuthenticationAccepted : Events.AuthenticationRejected);
        }

        public void AddJsonRpcClientConnected(int activeClientCount)
        {
            activity?.AddEvent(new ActivityEvent(Events.JsonRpcClientConnected, tags: new ActivityTagsCollection
            {
                [Tags.ActiveClientCount] = activeClientCount
            }));
        }

        public void AddJsonRpcConnectionClosed(string disconnectReason)
        {
            SetTag(Tags.DisconnectReason, disconnectReason);
            AddEvent(Events.JsonRpcConnectionClosed);
        }

        public void AddJsonRpcListening() => AddEvent(Events.JsonRpcListening);

        public void AddJsonRpcServerListening() => AddEvent(Events.JsonRpcServerListening);

        public void SetAssemblyCacheHit(bool cacheHit) => SetTag(Tags.AssemblyCacheHit, cacheHit);

        public void SetAssemblyCount(int count) => SetTag(Tags.AssemblyCount, count);

        public void SetAssemblyRequestedNames(IReadOnlyList<string> assemblyNames)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetTag(Tags.AssemblyRequestedNames, SanitizeAssemblyNames(assemblyNames));
        }

        public void SetAssemblyLoadedNames(IReadOnlyList<Assembly> assemblies)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetTag(Tags.AssemblyLoadedNames, SanitizeAssemblyNames(assemblies.Select(assembly => assembly.GetName().Name)));
        }

        public void SetAtsCounts(int capabilityCount, int handleTypeCount, int dtoTypeCount, int enumTypeCount, int exportedValueCount, int diagnosticCount)
        {
            SetTag(Tags.CapabilityCount, capabilityCount);
            SetTag(Tags.HandleTypeCount, handleTypeCount);
            SetTag(Tags.DtoTypeCount, dtoTypeCount);
            SetTag(Tags.EnumTypeCount, enumTypeCount);
            SetTag(Tags.ExportedValueCount, exportedValueCount);
            SetTag(Tags.DiagnosticCount, diagnosticCount);
        }

        public void SetCapability(string capabilityId, AtsCapabilityInfo? capability)
        {
            SetTag(Tags.CapabilityId, capabilityId);
            SetTag(Tags.CapabilityPackage, GetCapabilityPackage(capabilityId));
            SetTag(Tags.CapabilityKind, capability?.CapabilityKind.ToString());
        }

        public void SetCapabilityInvocation(string capabilityId, JsonObject? args)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetTag(Tags.CapabilityId, capabilityId);
            activity.SetTag(Tags.CapabilityPackage, GetCapabilityPackage(capabilityId));
            activity.SetTag(Tags.CapabilityArgumentCount, args?.Count ?? 0);
            activity.SetTag(Tags.CapabilityArgumentNames, SanitizeArgumentNames(args));
        }

        public void SetCapabilityScanFirstScan(bool firstScan) => SetTag(Tags.CapabilityScanFirstScan, firstScan);

        public void SetDetectionMatched(bool matched) => SetTag(Tags.DetectionMatched, matched);

        public void SetError(Exception exception)
        {
            if (activity is null)
            {
                return;
            }

            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddEvent(new ActivityEvent(Events.Exception, tags: new ActivityTagsCollection
            {
                [Tags.ExceptionType] = exception.GetType().FullName,
                [Tags.ExceptionMessage] = exception.Message
            }));
        }

        public void SetError(string description) => activity?.SetStatus(ActivityStatusCode.Error, description);

        public void SetFileCount(int count) => SetTag(Tags.FileCount, count);

        public void SetJsonRpcCall(string methodName, bool streaming)
        {
            SetTag(Tags.JsonRpcMethod, methodName);
            SetTag(Tags.JsonRpcStreaming, streaming);
        }

        public void SetLanguage(string? language) => SetTag(Tags.Language, language);

        public void SetTransport(string transport) => SetTag(Tags.Transport, transport);

        public void Dispose()
        {
            activity?.Dispose();
        }

        private void AddEvent(string name) => activity?.AddEvent(new ActivityEvent(name));

        private void SetTag(string key, object? value) => activity?.SetTag(key, value);

        private static string? GetCapabilityPackage(string capabilityId)
        {
            var separatorIndex = capabilityId.IndexOf('/');
            return separatorIndex > 0 ? capabilityId[..separatorIndex] : null;
        }

        private static string[] SanitizeArgumentNames(JsonObject? args)
        {
            return args is null
                ? []
                : [.. args.Select(arg => arg.Key).Order(StringComparer.Ordinal)];
        }

        private static string[] SanitizeAssemblyNames(IEnumerable<string?> assemblyNames)
        {
            return [.. assemblyNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
    }
}
