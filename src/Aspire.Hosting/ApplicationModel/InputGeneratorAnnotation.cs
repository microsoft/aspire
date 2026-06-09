// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Annotation for customizing the input generation for a parameter.
/// </summary>
/// <param name="inputGenerator">The function that generates the input for the parameter.</param>
public class InputGeneratorAnnotation(Func<ParameterResource, InteractionInput> inputGenerator) : IResourceAnnotation
{
    /// <summary>
    /// Gets the function that generates the input for the parameter.
    /// </summary>
    public Func<ParameterResource, InteractionInput> InputGenerator => inputGenerator;
}
