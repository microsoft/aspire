// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a specified C# project or file-based app.
/// </summary>
/// <param name="name">The name of the resource.</param>
public class CSharpAppResource(string name)
    : ProjectResource(name)
{

}
