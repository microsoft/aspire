// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Helper for resolving generic methods from runtime arguments.
/// </summary>
internal static class GenericMethodResolver
{
    /// <summary>
    /// Creates a closed generic method by inferring type arguments from the provided argument values.
    /// </summary>
    /// <param name="method">The open generic method.</param>
    /// <param name="arguments">The actual argument values.</param>
    /// <returns>A closed generic method with resolved type arguments.</returns>
    public static MethodInfo MakeGenericMethodFromArgs(MethodInfo method, object?[] arguments)
    {
        if (!method.ContainsGenericParameters)
        {
            return method;
        }

        var genericArgs = method.GetGenericArguments();
        var resolvedTypes = new Type[genericArgs.Length];
        var parameters = method.GetParameters();

        // Infer each generic argument from the actual argument types
        for (var i = 0; i < genericArgs.Length; i++)
        {
            resolvedTypes[i] = InferGenericArgument(genericArgs[i], parameters, arguments) ?? typeof(object);
        }

        return method.MakeGenericMethod(resolvedTypes);
    }

    private static Type? InferGenericArgument(Type genericArg, ParameterInfo[] parameters, object?[] arguments)
    {
        for (var j = 0; j < parameters.Length && j < arguments.Length; j++)
        {
            var paramType = parameters[j].ParameterType;
            var argument = arguments[j];

            if (argument == null)
            {
                continue;
            }

            // Direct match: parameter is the generic argument itself (e.g., T param)
            if (paramType == genericArg)
            {
                return argument.GetType();
            }

            // Generic type match: extract from IResourceBuilder<T>, etc.
            if (paramType.IsGenericType)
            {
                var typeArgs = paramType.GetGenericArguments();
                for (var k = 0; k < typeArgs.Length; k++)
                {
                    if (typeArgs[k] == genericArg)
                    {
                        var argType = argument.GetType();

                        // Try direct generic type match
                        if (argType.IsGenericType)
                        {
                            return argType.GetGenericArguments()[k];
                        }

                        // Try to find matching interface
                        var iface = argType.GetInterfaces()
                            .FirstOrDefault(i => i.IsGenericType &&
                                i.GetGenericTypeDefinition() == paramType.GetGenericTypeDefinition());

                        if (iface != null)
                        {
                            return iface.GetGenericArguments()[k];
                        }
                    }
                }
            }
        }

        return null;
    }
}
