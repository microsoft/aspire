// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Yaml;

/// <summary>
/// Sometimes, an empty string is an expected value in YAML charts. For example,
/// an empty string has meaning in Kubernetes Persistent Volume and Persistent Volume Claims.
/// An empty string means "use a classless volume" while null means "use the cluster's default
/// storage class." This attribute is useful for opting specific properties out of the 
/// <see cref="YamlIEnumerableSkipEmptyObjectGraphVisitor"/> filter that excludes empty
/// collections from the serialization process.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class PreserveEmptyStringAttribute : Attribute
{
}