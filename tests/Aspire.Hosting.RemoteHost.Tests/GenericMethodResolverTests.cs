// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Ats;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class GenericMethodResolverTests
{
    [Fact]
    public void MakeGenericMethodFromArgs_ReturnsOriginalMethodForNonGenericMethod()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.NonGenericMethod))!;

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, ["test"]);

        Assert.Same(method, result);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_InfersTypeFromDirectParameter()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericIdentity))!;

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, ["hello"]);

        Assert.False(result.ContainsGenericParameters);
        Assert.Equal(typeof(string), result.GetGenericArguments()[0]);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_InfersTypeFromIntParameter()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericIdentity))!;

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, [42]);

        Assert.False(result.ContainsGenericParameters);
        Assert.Equal(typeof(int), result.GetGenericArguments()[0]);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_InfersTypeFromGenericWrapper()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericFromWrapper))!;
        var wrapper = new Wrapper<string>("test");

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, [wrapper]);

        Assert.False(result.ContainsGenericParameters);
        Assert.Equal(typeof(string), result.GetGenericArguments()[0]);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_InfersTypeFromInterface()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericFromInterface))!;
        var impl = new IntWrapper(42);

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, [impl]);

        Assert.False(result.ContainsGenericParameters);
        Assert.Equal(typeof(int), result.GetGenericArguments()[0]);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_FallsBackToObjectForNullArgument()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericIdentity))!;

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, [null]);

        Assert.False(result.ContainsGenericParameters);
        Assert.Equal(typeof(object), result.GetGenericArguments()[0]);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_HandlesMultipleTypeParameters()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericPair))!;

        var result = GenericMethodResolver.MakeGenericMethodFromArgs(method, ["key", 42]);

        Assert.False(result.ContainsGenericParameters);
        var typeArgs = result.GetGenericArguments();
        Assert.Equal(typeof(string), typeArgs[0]);
        Assert.Equal(typeof(int), typeArgs[1]);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_CanInvokeResolvedMethod()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericIdentity))!;
        var resolved = GenericMethodResolver.MakeGenericMethodFromArgs(method, ["hello"]);

        var result = resolved.Invoke(null, ["hello"]);

        Assert.Equal("hello", result);
    }

    [Fact]
    public void MakeGenericMethodFromArgs_CanInvokeResolvedMethodWithWrapper()
    {
        var method = typeof(TestMethods).GetMethod(nameof(TestMethods.GenericFromWrapper))!;
        var wrapper = new Wrapper<int>(42);
        var resolved = GenericMethodResolver.MakeGenericMethodFromArgs(method, [wrapper]);

        var result = resolved.Invoke(null, [wrapper]);

        Assert.Equal(42, result);
    }

    private static class TestMethods
    {
        public static string NonGenericMethod(string value) => value;

        public static T GenericIdentity<T>(T value) => value;

        public static T GenericFromWrapper<T>(Wrapper<T> wrapper) => wrapper.Value;

        public static T GenericFromInterface<T>(IWrapper<T> wrapper) => wrapper.Value;

        public static (TKey, TValue) GenericPair<TKey, TValue>(TKey key, TValue value) => (key, value);
    }

    public interface IWrapper<T>
    {
        T Value { get; }
    }

    public class Wrapper<T>(T value) : IWrapper<T>
    {
        public T Value { get; } = value;
    }

    private sealed class IntWrapper(int value) : IWrapper<int>
    {
        public int Value { get; } = value;
    }
}
