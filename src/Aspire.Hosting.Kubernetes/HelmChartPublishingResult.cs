// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Describes a generated Helm chart and the value mappings needed by an external deployment publisher.
/// </summary>
/// <remarks>
/// This is a read-only snapshot of publishing metadata, not of the chart files or the application model.
/// Resource and value-provider objects retain their original identity and are not evaluated or cloned.
/// Secret parameters and provisioned outputs remain deferred until the consuming publisher resolves them.
/// </remarks>
public sealed class HelmChartPublishingResult
{
    internal HelmChartPublishingResult(string outputPath, KubernetesEnvironmentResource environment)
    {
        OutputPath = outputPath;

        // Repeated chart generation can capture the same mapping more than once.
        // Copy distinct entries without exposing or modifying the publisher's capture buffers.
        Parameters = Array.AsReadOnly(environment.CapturedHelmValues.Distinct()
            .Select(value => new HelmValueMapping<ParameterResource>(value.Section, value.ResourceKey, value.ValueKey, value.Parameter))
            .ToArray());
        ValueProviders = Array.AsReadOnly(environment.CapturedHelmValueProviders.Distinct()
            .Select(value => new HelmValueMapping<IValueProvider>(value.Section, value.ResourceKey, value.ValueKey, value.ValueProvider))
            .ToArray());
        Images = Array.AsReadOnly(environment.CapturedHelmImageReferences.Distinct()
            .Select(value => new HelmValueMapping<IResource>(value.Section, value.ResourceKey, value.ValueKey, value.Resource))
            .ToArray());
        CrossReferences = Array.AsReadOnly(environment.CapturedHelmCrossReferences.Distinct()
            .Select(value => new HelmValueMapping<string>(value.Section, value.ResourceKey, value.ValueKey, value.TemplateValue))
            .ToArray());
    }

    /// <summary>
    /// Gets the directory containing <c>Chart.yaml</c>, <c>values.yaml</c>, and the chart templates.
    /// </summary>
    public string OutputPath { get; }

    /// <summary>
    /// Gets the mappings to parameters that require deployment-time resolution, including secret parameters.
    /// </summary>
    public IReadOnlyList<HelmValueMapping<ParameterResource>> Parameters { get; }

    /// <summary>
    /// Gets the mappings to deferred value providers, such as provisioned outputs.
    /// </summary>
    public IReadOnlyList<HelmValueMapping<IValueProvider>> ValueProviders { get; }

    /// <summary>
    /// Gets the mappings to resources whose container images must be resolved at deployment time.
    /// </summary>
    /// <remarks>
    /// Use <see cref="ContainerImageReference"/> to resolve an image after its registry is available.
    /// </remarks>
    public IReadOnlyList<HelmValueMapping<IResource>> Images { get; }

    /// <summary>
    /// Gets the mappings to composite templates containing references to other Helm values.
    /// </summary>
    /// <remarks>
    /// For example, a template can contain <c>{{ .Values.secrets.cache.password }}</c>.
    /// Resolve the referenced parameter mappings before substituting these expressions.
    /// </remarks>
    public IReadOnlyList<HelmValueMapping<string>> CrossReferences { get; }
}
