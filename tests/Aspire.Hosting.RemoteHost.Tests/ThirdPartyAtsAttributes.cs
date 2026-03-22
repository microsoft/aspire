// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Third-party mock attribute types defined in a separate namespace.
// These mirror the shape of the official Aspire.Hosting ATS attributes,
// but their different namespace makes them negative test cases for
// full-name matching.

namespace Aspire.Hosting.RemoteHost.Tests.ThirdParty;

[AttributeUsage(
    AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Assembly | AttributeTargets.Property,
    Inherited = false,
    AllowMultiple = true)]
public sealed class AspireExportAttribute : Attribute
{
    public AspireExportAttribute(string id)
    {
        Id = id;
    }

    public AspireExportAttribute()
    {
    }

    public AspireExportAttribute(Type type)
    {
        Type = type;
    }

    public string? Id { get; }
    public Type? Type { get; set; }
    public string? Description { get; set; }
    public string? MethodName { get; set; }
    public bool ExposeProperties { get; set; }
    public bool ExposeMethods { get; set; }
}

[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = false)]
public sealed class AspireExportIgnoreAttribute : Attribute
{
    public string? Reason { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class AspireDtoAttribute : Attribute
{
    public string? DtoTypeId { get; set; }
}

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false)]
public sealed class AspireUnionAttribute : Attribute
{
    public AspireUnionAttribute(params Type[] types)
    {
        Types = types;
    }

    public Type[] Types { get; }
}
