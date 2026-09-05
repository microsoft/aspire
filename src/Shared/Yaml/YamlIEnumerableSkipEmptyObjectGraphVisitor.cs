// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.ObjectGraphVisitors;

namespace Aspire.Hosting.Yaml;

/// <summary>
/// A specialized implementation of <see cref="ChainedObjectGraphVisitor"/> designed to
/// handle the serialization of object graphs into YAML while skipping empty collections.
/// </summary>
/// <remarks>
/// This visitor intercepts the serialization process for mapping objects, including collections,
/// and ensures that empty collections are excluded from the output YAML representation. Non-collection objects
/// and non-empty collections are processed normally by delegating to the underlying visitor.
/// </remarks>
/// <example>
/// This class is internally integrated in YAML serialization pipelines to optimize representation
/// by excluding unnecessary empty elements, thereby simplifying the resulting YAML document.
/// </example>
internal sealed class YamlIEnumerableSkipEmptyObjectGraphVisitor(
    IObjectGraphVisitor<IEmitter> nextVisitor
) : ChainedObjectGraphVisitor(nextVisitor)
{
    /// <summary>
    /// Processes the entry of a mapping node during YAML serialization.
    /// Determines if a mapping object, including collections, should be serialized
    /// based on its contents. Skips empty collections from serialization while processing
    /// other objects or non-empty collections normally.
    /// </summary>
    /// <param name="key">The descriptor providing metadata about the property being serialized.</param>
    /// <param name="value">The descriptor providing metadata and value of the object to be serialized.</param>
    /// <param name="context">The emitter used to write the YAML output.</param>
    /// <param name="serializer">The object responsible for serializing the value.</param>
    /// <returns>
    /// A boolean indicating whether the mapping node should be processed. Returns false
    /// for empty collections, and true for other objects or non-empty collections.
    /// </returns>
    public override bool EnterMapping(
        IPropertyDescriptor key,
        IObjectDescriptor value,
        IEmitter context,
        ObjectSerializer serializer)
    {
        var retVal = false;

        if (key.GetCustomAttribute<PreserveEmptyStringAttribute>() is { })
        {
            return base.EnterMapping(key, value, context, serializer);
        }

        switch (value.Value)
        {
            case null:
                return false;
            case IEnumerable enumerableObject:
            {
                var enumerator = enumerableObject.GetEnumerator();
                using var _ = enumerator as IDisposable;
                if (enumerator.MoveNext())
                {
                    retVal = base.EnterMapping(key, value, context, serializer);
                }
                break;
            }
            default:
                retVal = base.EnterMapping(key, value, context, serializer);
                break;
        }

        return retVal;
    }
}
