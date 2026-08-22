// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures the Azure Front Door origin group that fronts one backend application, including how traffic
/// is distributed across the application's regional stamps.
/// </summary>
/// <remarks>
/// An origin group corresponds to one backend application. When that application is deployed as several
/// regional stamps, the origin group holds one origin per stamp, and Front Door health-probes and
/// load-balances across them behind a single global hostname.
/// </remarks>
[AspireExport(ExposeMethods = true)]
public sealed class AzureFrontDoorOriginGroupBuilder
{
    private readonly Dictionary<string, int> _stampPriorities = [];
    private readonly Dictionary<string, int> _stampWeights = [];

    internal AzureFrontDoorOriginGroupBuilder()
    {
    }

    internal FrontDoorOriginRouting Routing { get; private set; } = FrontDoorOriginRouting.LatencyBased;

    internal string? ProbePath { get; private set; }

    internal FrontDoorHealthProbeProtocol? ProbeProtocol { get; private set; }

    internal TimeSpan? ProbeInterval { get; private set; }

    internal int? SampleSize { get; private set; }

    internal int? SuccessfulSamplesRequired { get; private set; }

    internal int? AdditionalLatencyMilliseconds { get; private set; }

    internal bool? SessionAffinityEnabled { get; private set; }

    internal TimeSpan? TrafficRestorationTime { get; private set; }

    internal string? CustomDomainHostName { get; private set; }

    /// <summary>
    /// Sets how traffic is distributed across the origins of this origin group.
    /// </summary>
    /// <param name="routing">The routing method.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// <example>
    /// Serve every region simultaneously, closest-first:
    /// <code lang="C#">
    /// frontDoor.WithOriginGroup(api, g => g.WithRouting(FrontDoorOriginRouting.LatencyBased));
    /// </code>
    /// </example>
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithRouting(FrontDoorOriginRouting routing)
    {
        Routing = routing;
        return this;
    }

    /// <summary>
    /// Sets the routing priority of the stamp deployed to the specified compute environment.
    /// </summary>
    /// <param name="computeEnvironment">The compute environment identifying the stamp.</param>
    /// <param name="priority">The priority, from 1 (most preferred) to 5.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// Front Door sends traffic to the healthy origins with the lowest priority number and treats higher
    /// numbers as backups. Setting a priority implies <see cref="FrontDoorOriginRouting.Failover"/> semantics
    /// for this origin group even when the routing method was not changed.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithStampPriority(IResourceBuilder<IComputeEnvironmentResource> computeEnvironment, int priority)
    {
        ArgumentNullException.ThrowIfNull(computeEnvironment);
        // Azure rejects values outside this range, so fail in the app model rather than at deployment time.
        ArgumentOutOfRangeException.ThrowIfLessThan(priority, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 5);

        _stampPriorities[computeEnvironment.Resource.Name] = priority;
        return this;
    }

    /// <summary>
    /// Sets the routing weight of the stamp deployed to the specified compute environment.
    /// </summary>
    /// <param name="computeEnvironment">The compute environment identifying the stamp.</param>
    /// <param name="weight">The weight, from 1 to 1000.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// Weights only apply between origins that share the same priority. Setting a weight implies
    /// <see cref="FrontDoorOriginRouting.Weighted"/> semantics for this origin group even when the routing
    /// method was not changed.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithStampWeight(IResourceBuilder<IComputeEnvironmentResource> computeEnvironment, int weight)
    {
        ArgumentNullException.ThrowIfNull(computeEnvironment);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 1000);

        _stampWeights[computeEnvironment.Resource.Name] = weight;
        return this;
    }

    /// <summary>
    /// Configures the health probe Front Door uses to decide whether each origin is healthy.
    /// </summary>
    /// <param name="path">The path to probe, for example <c>/health</c>.</param>
    /// <param name="protocol">The protocol to probe with.</param>
    /// <param name="interval">How often each origin is probed.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// When this is not called, the probe settings come from the origin resource's
    /// <c>WithHttpProbe</c> annotation, falling back to an HTTPS probe of <c>/</c>.
    /// Health probing is what makes regional failover work, so probe a path that reflects the health of the
    /// whole stamp rather than one that always returns success.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithHealthProbe(string path, FrontDoorHealthProbeProtocol protocol, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        ProbePath = path;
        ProbeProtocol = protocol;
        ProbeInterval = interval;
        return this;
    }

    /// <summary>
    /// Configures how Front Door samples origin latency when choosing between origins.
    /// </summary>
    /// <param name="sampleSize">The number of samples to consider for load balancing decisions.</param>
    /// <param name="successfulSamplesRequired">The number of samples within the sample window that must succeed for an origin to be considered healthy.</param>
    /// <param name="additionalLatencyMilliseconds">
    /// The latency band, in milliseconds. Origins whose latency is within this many milliseconds of the
    /// fastest origin are all considered equally close, and traffic is spread across them.
    /// </param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// Widening <paramref name="additionalLatencyMilliseconds"/> spreads traffic across more regions;
    /// narrowing it pins traffic to the closest region.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithLoadBalancing(int sampleSize, int successfulSamplesRequired, int additionalLatencyMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(successfulSamplesRequired, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successfulSamplesRequired, sampleSize);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalLatencyMilliseconds);

        SampleSize = sampleSize;
        SuccessfulSamplesRequired = successfulSamplesRequired;
        AdditionalLatencyMilliseconds = additionalLatencyMilliseconds;
        return this;
    }

    /// <summary>
    /// Enables or disables session affinity, which pins a client to the origin that served its first request.
    /// </summary>
    /// <param name="enabled"><see langword="true"/> to enable session affinity; otherwise <see langword="false"/>.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// Session affinity keeps a client in one regional stamp, which is useful for stateful backends but
    /// works against latency-based routing when clients move between regions.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithSessionAffinity(bool enabled)
    {
        SessionAffinityEnabled = enabled;
        return this;
    }

    /// <summary>
    /// Sets how gradually traffic is shifted back to an origin that has become healthy again.
    /// </summary>
    /// <param name="trafficRestorationTime">The restoration window, from 0 to 50 minutes.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// A non-zero window ramps traffic back up over time, so a region that has just recovered is not
    /// immediately hit with its full share of load.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithTrafficRestorationTime(TimeSpan trafficRestorationTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trafficRestorationTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trafficRestorationTime, TimeSpan.FromMinutes(50));

        TrafficRestorationTime = trafficRestorationTime;
        return this;
    }

    /// <summary>
    /// Serves this application from a custom domain in addition to the generated <c>*.azurefd.net</c> hostname.
    /// </summary>
    /// <param name="hostName">The fully qualified domain name, for example <c>www.contoso.com</c>.</param>
    /// <returns>The origin group builder for chaining.</returns>
    /// <ats-returns>The origin group builder.</ats-returns>
    /// <remarks>
    /// The domain must be validated by creating the DNS records Azure asks for; the required TXT validation
    /// token is emitted as a Bicep output named <c>{origin}_customDomainValidationToken</c>.
    /// A custom domain is the only public hostname known before deployment, so it is what backend
    /// applications should be told about when they need to know their own public address. Referencing the
    /// generated Front Door hostname from a backend would create a Bicep module cycle, because Front Door
    /// already depends on the backend's host address.
    /// </remarks>
    public AzureFrontDoorOriginGroupBuilder WithCustomDomain(string hostName)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);

        CustomDomainHostName = hostName;
        return this;
    }

    /// <summary>
    /// Gets the priority configured for the stamp in the specified compute environment, if any.
    /// </summary>
    internal int? GetPriority(IComputeEnvironmentResource computeEnvironment) =>
        _stampPriorities.TryGetValue(computeEnvironment.Name, out var priority) ? priority : null;

    /// <summary>
    /// Gets the weight configured for the stamp in the specified compute environment, if any.
    /// </summary>
    internal int? GetWeight(IComputeEnvironmentResource computeEnvironment) =>
        _stampWeights.TryGetValue(computeEnvironment.Name, out var weight) ? weight : null;

    /// <summary>
    /// Gets a value indicating whether any per-stamp priority or weight was configured.
    /// </summary>
    internal bool HasStampOverrides => _stampPriorities.Count > 0 || _stampWeights.Count > 0;
}
