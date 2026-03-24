// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.TypeSystem;
using Aspire.Hosting.RemoteHost.Ats;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class AtsMarshallerTests
{
    private static AtsContext CreateTestContext()
    {
        return new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            DtoTypes = [
                new AtsDtoTypeInfo { TypeId = "test/TestDto", Name = "TestDto", ClrType = typeof(TestDto), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/TestDtoWithEnum", Name = "TestDtoWithEnum", ClrType = typeof(TestDtoWithEnum), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/TestDto", Name = "TestDto", ClrType = typeof(TestDto), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/SelfReferencingDto", Name = "SelfReferencingDto", ClrType = typeof(SelfReferencingDto), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/ParentDto", Name = "ParentDto", ClrType = typeof(ParentDto), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/ChildDto", Name = "ChildDto", ClrType = typeof(ChildDto), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/DtoWithJsonPropertyName", Name = "DtoWithJsonPropertyName", ClrType = typeof(DtoWithJsonPropertyName), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/DtoWithJsonIgnore", Name = "DtoWithJsonIgnore", ClrType = typeof(DtoWithJsonIgnore), Properties = [] },
                new AtsDtoTypeInfo { TypeId = "test/DtoWithReadOnlyProperty", Name = "DtoWithReadOnlyProperty", ClrType = typeof(DtoWithReadOnlyProperty), Properties = [] },
            ],
            EnumTypes = []
        };
    }

    private static AtsMarshaller CreateTestMarshaller(HandleRegistry? handles = null, CancellationTokenRegistry? ctRegistry = null)
    {
        handles ??= new HandleRegistry();
        ctRegistry ??= new CancellationTokenRegistry();
        var context = CreateTestContext();
        return new AtsMarshaller(handles, context, ctRegistry, new Lazy<AtsCallbackProxyFactory>(() => throw new NotImplementedException()));
    }

    private static AtsMarshaller CreateMarshaller(HandleRegistry? registry = null)
    {
        return CreateTestMarshaller(registry);
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(Guid))]
    public void IsSimpleType_ReturnsTrueForPrimitives(Type type)
    {
        Assert.True(AtsMarshaller.IsSimpleType(type));
    }

    [Fact]
    public void IsSimpleType_ReturnsTrueForNullablePrimitives()
    {
        Assert.True(AtsMarshaller.IsSimpleType(typeof(int?)));
        Assert.True(AtsMarshaller.IsSimpleType(typeof(bool?)));
        Assert.True(AtsMarshaller.IsSimpleType(typeof(DateTime?)));
    }

    [Fact]
    public void IsSimpleType_ReturnsTrueForEnums()
    {
        Assert.True(AtsMarshaller.IsSimpleType(typeof(TestEnum)));
    }

    [Fact]
    public void IsSimpleType_ReturnsFalseForComplexTypes()
    {
        Assert.False(AtsMarshaller.IsSimpleType(typeof(object)));
        Assert.False(AtsMarshaller.IsSimpleType(typeof(List<int>)));
        Assert.False(AtsMarshaller.IsSimpleType(typeof(TestClass)));
    }

    [Fact]
    public void MarshalToJson_ReturnsNullForNull()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(null);

        Assert.Null(result);
    }

    [Fact]
    public void MarshalToJson_MarshalsStringDirectly()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson("hello");

        Assert.NotNull(result);
        Assert.Equal("hello", result.GetValue<string>());
    }

    [Fact]
    public void MarshalToJson_MarshalsIntDirectly()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(42);

        Assert.NotNull(result);
        Assert.Equal(42, result.GetValue<int>());
    }

    [Fact]
    public void MarshalToJson_MarshalsBoolDirectly()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(true);

        Assert.NotNull(result);
        Assert.True(result.GetValue<bool>());
    }

    [Fact]
    public void MarshalToJson_MarshalsEnumAsString()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(TestEnum.ValueB);

        Assert.NotNull(result);
        Assert.Equal("ValueB", result.GetValue<string>());
    }

    [Fact]
    public void MarshalToJson_MarshalsTimeSpanAsMilliseconds()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(TimeSpan.FromSeconds(1.5));

        Assert.NotNull(result);
        Assert.Equal(1500.0, result.GetValue<double>());
    }

    [Fact]
    public void MarshalToJson_MarshalsArrayRecursively()
    {
        var marshaller = CreateMarshaller();
        var array = new[] { 1, 2, 3 };

        var result = marshaller.MarshalToJson(array);

        Assert.NotNull(result);
        Assert.IsType<JsonArray>(result);
        var jsonArray = (JsonArray)result;
        Assert.Equal(3, jsonArray.Count);
        Assert.Equal(1, jsonArray[0]!.GetValue<int>());
        Assert.Equal(2, jsonArray[1]!.GetValue<int>());
        Assert.Equal(3, jsonArray[2]!.GetValue<int>());
    }

    [Fact]
    public void MarshalToJson_MarshalsCancellationTokenAsTokenId()
    {
        using var registry = new CancellationTokenRegistry();
        var marshaller = CreateTestMarshaller(ctRegistry: registry);
        using var cts = new CancellationTokenSource();

        var result = marshaller.MarshalToJson(cts.Token);

        var tokenValue = Assert.IsAssignableFrom<JsonValue>(result);
        var tokenId = tokenValue.GetValue<string>();
        Assert.StartsWith("ct_", tokenId);
        Assert.True(registry.TryGetToken(tokenId, out var token));
        Assert.True(token.CanBeCanceled);
    }

    [Fact]
    public void MarshalToJson_WithCancellationTokenTypeRef_MarshalsAsTokenId()
    {
        using var registry = new CancellationTokenRegistry();
        var marshaller = CreateTestMarshaller(ctRegistry: registry);
        using var cts = new CancellationTokenSource();
        var typeRef = new AtsTypeRef
        {
            TypeId = AtsConstants.CancellationToken,
            Category = AtsTypeCategory.Primitive
        };

        var result = marshaller.MarshalToJson(cts.Token, typeRef);

        var tokenValue = Assert.IsAssignableFrom<JsonValue>(result);
        var tokenId = tokenValue.GetValue<string>();
        Assert.StartsWith("ct_", tokenId);
        Assert.True(registry.TryGetToken(tokenId, out var token));
        Assert.True(token.CanBeCanceled);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsStringCorrectly()
    {
        var value = JsonValue.Create("test");

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(string));

        Assert.Equal("test", result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsIntCorrectly()
    {
        var value = JsonValue.Create(42);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(int));

        Assert.Equal(42, result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsBoolCorrectly()
    {
        var value = JsonValue.Create(true);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(bool));

        Assert.Equal(true, result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsTimeSpanFromMilliseconds()
    {
        var value = JsonValue.Create(1500.0);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(TimeSpan));

        Assert.Equal(TimeSpan.FromMilliseconds(1500), result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsNullableInt()
    {
        var value = JsonValue.Create(42);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(int?));

        Assert.Equal(42, result);
    }

    [Fact]
    public void UnmarshalFromJson_ReturnsNullForNullNode()
    {
        var (marshaller, context) = CreateMarshallerWithContext();

        var result = marshaller.UnmarshalFromJson(null, typeof(string), context);

        Assert.Null(result);
    }

    [Fact]
    public void UnmarshalFromJson_ReturnsCancellationTokenNoneForNullNode()
    {
        var (marshaller, context) = CreateMarshallerWithContext();

        var result = marshaller.UnmarshalFromJson(null, typeof(CancellationToken), context);

        var token = Assert.IsType<CancellationToken>(result);
        Assert.Equal(CancellationToken.None, token);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsHandleReference()
    {
        var registry = new HandleRegistry();
        var obj = new TestClass { Value = 42 };
        var handleId = registry.Register(obj, "aspire/Test");
        var json = new JsonObject { ["$handle"] = handleId };
        var (marshaller, context) = CreateMarshallerWithContext(registry);

        var result = marshaller.UnmarshalFromJson(json, typeof(TestClass), context);

        Assert.Same(obj, result);
    }

    [Fact]
    public void UnmarshalFromJson_ThrowsForUnknownHandle()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonObject { ["$handle"] = "aspire/Unknown:999" };

        Assert.Throws<CapabilityException>(() =>
            marshaller.UnmarshalFromJson(json, typeof(object), context));
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsArray()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonArray { 1, 2, 3 };

        var result = marshaller.UnmarshalFromJson(json, typeof(int[]), context);

        Assert.NotNull(result);
        var array = Assert.IsType<int[]>(result);
        Assert.Equal(new[] { 1, 2, 3 }, array);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsList()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonArray { "a", "b", "c" };

        var result = marshaller.UnmarshalFromJson(json, typeof(List<string>), context);

        Assert.NotNull(result);
        var list = Assert.IsType<List<string>>(result);
        Assert.Equal(["a", "b", "c"], list);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsDictionary()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonObject { ["key1"] = 1, ["key2"] = 2 };

        var result = marshaller.UnmarshalFromJson(json, typeof(Dictionary<string, int>), context);

        Assert.NotNull(result);
        var dict = Assert.IsType<Dictionary<string, int>>(result);
        Assert.Equal(1, dict["key1"]);
        Assert.Equal(2, dict["key2"]);
    }

    // Additional primitive type tests
    [Theory]
    [InlineData(typeof(byte))]
    [InlineData(typeof(short))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(ushort))]
    [InlineData(typeof(sbyte))]
    [InlineData(typeof(char))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(DateOnly))]
    [InlineData(typeof(TimeOnly))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(Uri))]
    public void IsSimpleType_ReturnsTrueForAdditionalPrimitives(Type type)
    {
        Assert.True(AtsMarshaller.IsSimpleType(type));
    }

    // DateOnly marshalling tests
    [Fact]
    public void MarshalToJson_MarshalsDateOnlyAsIsoString()
    {
        var marshaller = CreateMarshaller();
        var dateOnly = new DateOnly(2024, 6, 15);

        var result = marshaller.MarshalToJson(dateOnly);

        Assert.NotNull(result);
        Assert.Equal("2024-06-15", result.GetValue<string>());
    }

    [Fact]
    public void ConvertPrimitive_ConvertsDateOnlyFromString()
    {
        var value = JsonValue.Create("2024-06-15");

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(DateOnly));

        Assert.Equal(new DateOnly(2024, 6, 15), result);
    }

    // TimeOnly marshalling tests
    [Fact]
    public void MarshalToJson_MarshalsTimeOnlyAsIsoString()
    {
        var marshaller = CreateMarshaller();
        var timeOnly = new TimeOnly(14, 30, 45);

        var result = marshaller.MarshalToJson(timeOnly);

        Assert.NotNull(result);
        Assert.Contains("14:30:45", result.GetValue<string>());
    }

    [Fact]
    public void ConvertPrimitive_ConvertsTimeOnlyFromString()
    {
        var value = JsonValue.Create("14:30:45");

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(TimeOnly));

        Assert.Equal(new TimeOnly(14, 30, 45), result);
    }

    [Fact]
    public void MarshalToJson_MarshalsLong()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(9223372036854775807L);

        Assert.NotNull(result);
        Assert.Equal(9223372036854775807L, result.GetValue<long>());
    }

    [Fact]
    public void MarshalToJson_MarshalsDouble()
    {
        var marshaller = CreateMarshaller();

        var result = marshaller.MarshalToJson(3.14159);

        Assert.NotNull(result);
        Assert.Equal(3.14159, result.GetValue<double>());
    }

    [Fact]
    public void MarshalToJson_MarshalsGuid()
    {
        var marshaller = CreateMarshaller();
        var guid = Guid.NewGuid();

        var result = marshaller.MarshalToJson(guid);

        Assert.NotNull(result);
    }

    [Fact]
    public void MarshalToJson_MarshalsListAsHandle()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var list = new List<int> { 1, 2, 3 };

        var result = marshaller.MarshalToJson(list);

        Assert.NotNull(result);
        Assert.IsType<JsonObject>(result);
        var jsonObj = (JsonObject)result;
        Assert.NotNull(jsonObj["$handle"]);
        Assert.NotNull(jsonObj["$type"]);
        // Type ID is derived from assembly and type name
        var typeId = jsonObj["$type"]!.GetValue<string>();
        Assert.Contains("List", typeId);
    }

    [Fact]
    public void MarshalToJson_MarshalsDictionaryAsHandle()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var dict = new Dictionary<string, int> { ["a"] = 1 };

        var result = marshaller.MarshalToJson(dict);

        Assert.NotNull(result);
        Assert.IsType<JsonObject>(result);
        var jsonObj = (JsonObject)result;
        Assert.NotNull(jsonObj["$handle"]);
        Assert.NotNull(jsonObj["$type"]);
        // Type ID uses special format for dictionary handles: Dict<K,V>
        var typeId = jsonObj["$type"]!.GetValue<string>();
        Assert.Contains("Dict<", typeId);
    }

    [Fact]
    public void MarshalToJson_MarshalsComplexObjectAsHandle()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var obj = new TestClass { Value = 42 };

        var result = marshaller.MarshalToJson(obj);

        Assert.NotNull(result);
        Assert.IsType<JsonObject>(result);
        var jsonObj = (JsonObject)result;
        Assert.NotNull(jsonObj["$handle"]);
        Assert.NotNull(jsonObj["$type"]);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsLong()
    {
        var value = JsonValue.Create(9223372036854775807L);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(long));

        Assert.Equal(9223372036854775807L, result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsDouble()
    {
        var value = JsonValue.Create(3.14159);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(double));

        Assert.Equal(3.14159, result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsFloat()
    {
        var value = JsonValue.Create(3.14);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(float));

        Assert.Equal(3.14f, (float)result!, 2);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsDecimal()
    {
        var value = JsonValue.Create(123.456m);

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(decimal));

        Assert.Equal(123.456m, result);
    }

    [Fact]
    public void ConvertPrimitive_ConvertsTimeSpanFromString()
    {
        var value = JsonValue.Create("01:30:00");

        var result = AtsMarshaller.ConvertPrimitive(value!, typeof(TimeSpan));

        Assert.Equal(TimeSpan.FromHours(1.5), result);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsIList()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonArray { 1, 2, 3 };

        var result = marshaller.UnmarshalFromJson(json, typeof(IList<int>), context);

        Assert.NotNull(result);
        var list = Assert.IsAssignableFrom<IList<int>>(result);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsIEnumerable()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonArray { "x", "y" };

        var result = marshaller.UnmarshalFromJson(json, typeof(IEnumerable<string>), context);

        Assert.NotNull(result);
        var enumerable = Assert.IsAssignableFrom<IEnumerable<string>>(result);
        Assert.Equal(2, enumerable.Count());
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsIDictionary()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonObject { ["a"] = 1, ["b"] = 2 };

        var result = marshaller.UnmarshalFromJson(json, typeof(IDictionary<string, int>), context);

        Assert.NotNull(result);
        var dict = Assert.IsAssignableFrom<IDictionary<string, int>>(result);
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public void UnmarshalFromJson_ThrowsForNonDtoObject()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonObject { ["value"] = 42 };

        // TestClass doesn't have [AspireDto] attribute
        var ex = Assert.Throws<CapabilityException>(() =>
            marshaller.UnmarshalFromJson(json, typeof(TestClass), context));

        Assert.Contains("AspireDto", ex.Message);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsDto()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = new JsonObject { ["name"] = "test", ["count"] = 5 };

        var result = marshaller.UnmarshalFromJson(json, typeof(TestDto), context);

        Assert.NotNull(result);
        var dto = Assert.IsType<TestDto>(result);
        Assert.Equal("test", dto.Name);
        Assert.Equal(5, dto.Count);
    }

    [Fact]
    public void MarshalToJson_MarshalsDto()
    {
        var marshaller = CreateMarshaller();
        var dto = new TestDto { Name = "test", Count = 10 };

        var result = marshaller.MarshalToJson(dto);

        Assert.NotNull(result);
        Assert.IsType<JsonObject>(result);
        var jsonObj = (JsonObject)result;
        Assert.Equal("test", jsonObj["name"]?.GetValue<string>());
        Assert.Equal(10, jsonObj["count"]?.GetValue<int>());
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsEnumFromString()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = JsonValue.Create("ValueB");

        var result = marshaller.UnmarshalFromJson(json, typeof(TestEnum), context);

        Assert.Equal(TestEnum.ValueB, result);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsEnumFromStringCaseInsensitive()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = JsonValue.Create("valueb"); // lowercase

        var result = marshaller.UnmarshalFromJson(json, typeof(TestEnum), context);

        Assert.Equal(TestEnum.ValueB, result);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsEnumFromNumericValue()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = JsonValue.Create(1); // ValueB is index 1

        var result = marshaller.UnmarshalFromJson(json, typeof(TestEnum), context);

        Assert.Equal(TestEnum.ValueB, result);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsNullableEnumFromString()
    {
        var (marshaller, context) = CreateMarshallerWithContext();
        var json = JsonValue.Create("ValueA");

        var result = marshaller.UnmarshalFromJson(json, typeof(TestEnum?), context);

        Assert.Equal(TestEnum.ValueA, result);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsNullableEnumFromNull()
    {
        var (marshaller, context) = CreateMarshallerWithContext();

        var result = marshaller.UnmarshalFromJson(null, typeof(TestEnum?), context);

        Assert.Null(result);
    }

    private static (AtsMarshaller Marshaller, AtsMarshaller.UnmarshalContext Context) CreateMarshallerWithContext(HandleRegistry? registry = null)
    {
        var handles = registry ?? new HandleRegistry();
        var context = new AtsMarshaller.UnmarshalContext
        {
            CapabilityId = "test/capability",
            ParameterName = "testParam"
        };
        var marshaller = CreateTestMarshaller(handles);
        return (marshaller, context);
    }

    [Fact]
    public void MarshalToJson_HandlesDirectCircularReference()
    {
        var marshaller = CreateMarshaller();
        var dto = new SelfReferencingDto { Name = "root" };
        dto.Self = dto;

        var result = marshaller.MarshalToJson(dto);

        Assert.NotNull(result);
        var obj = Assert.IsType<JsonObject>(result);
        Assert.Equal("root", obj["name"]!.GetValue<string>());
        Assert.True(obj["self"] is null || obj["self"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void MarshalToJson_HandlesMutualCircularReference()
    {
        var marshaller = CreateMarshaller();
        var parent = new ParentDto { Label = "parent" };
        var child = new ChildDto { Label = "child", Parent = parent };
        parent.Child = child;

        var result = marshaller.MarshalToJson(parent);

        Assert.NotNull(result);
        var obj = Assert.IsType<JsonObject>(result);
        Assert.Equal("parent", obj["label"]!.GetValue<string>());
        var childObj = Assert.IsType<JsonObject>(obj["child"]);
        Assert.Equal("child", childObj["label"]!.GetValue<string>());
        // The back-reference to parent should be null (cycle broken)
        Assert.True(childObj["parent"] is null || childObj["parent"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void MarshalToJson_HandlesCircularReferenceWithTypeRef()
    {
        var marshaller = CreateMarshaller();
        var dto = new SelfReferencingDto { Name = "typed" };
        dto.Self = dto;

        var typeRef = new AtsTypeRef { TypeId = "test/SelfReferencingDto", Category = AtsTypeCategory.Dto };

        var result = marshaller.MarshalToJson(dto, typeRef);

        Assert.NotNull(result);
        var obj = Assert.IsType<JsonObject>(result);
        Assert.Equal("typed", obj["name"]!.GetValue<string>());
        Assert.True(obj["self"] is null || obj["self"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void MarshalToJson_PreservesNonCircularNestedDtos()
    {
        var marshaller = CreateMarshaller();
        var parent = new ParentDto { Label = "parent" };
        var child = new ChildDto { Label = "child", Parent = null };
        parent.Child = child;

        var result = marshaller.MarshalToJson(parent);

        Assert.NotNull(result);
        var obj = Assert.IsType<JsonObject>(result);
        Assert.Equal("parent", obj["label"]!.GetValue<string>());
        var childObj = Assert.IsType<JsonObject>(obj["child"]);
        Assert.Equal("child", childObj["label"]!.GetValue<string>());
        Assert.True(childObj["parent"] is null || childObj["parent"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void ApplyDtoProperties_UpdatesWritableProperties()
    {
        var marshaller = CreateMarshaller();
        var dto = new TestDto { Name = "original", Count = 0 };
        var source = new JsonObject { ["name"] = "updated", ["count"] = 42 };

        marshaller.ApplyDtoProperties(source, dto, typeof(TestDto));

        Assert.Equal("updated", dto.Name);
        Assert.Equal(42, dto.Count);
    }

    [Fact]
    public void ApplyDtoProperties_OnlyUpdatesProvidedProperties()
    {
        var marshaller = CreateMarshaller();
        var dto = new TestDto { Name = "original", Count = 10 };
        var source = new JsonObject { ["name"] = "updated" };

        marshaller.ApplyDtoProperties(source, dto, typeof(TestDto));

        Assert.Equal("updated", dto.Name);
        Assert.Equal(10, dto.Count); // Count unchanged since not in JSON
    }

    [Fact]
    public void ApplyDtoProperties_SkipsNonDeserializableProperties()
    {
        var marshaller = CreateMarshaller();
        var dto = new DtoWithComplexProperty { Name = "original", Complex = new NonDeserializableType("keep-me") };
        var source = new JsonObject
        {
            ["name"] = "updated",
            ["complex"] = new JsonObject { ["value"] = "should-be-ignored" }
        };

        marshaller.ApplyDtoProperties(source, dto, typeof(DtoWithComplexProperty));

        Assert.Equal("updated", dto.Name);
        Assert.Equal("keep-me", dto.Complex.Value); // Complex property unchanged
    }

    [Fact]
    public void ApplyDtoProperties_RespectsJsonPropertyName()
    {
        var marshaller = CreateMarshaller();
        var dto = new DtoWithJsonPropertyName { DisplayName = "original", Value = 0 };
        var source = new JsonObject { ["display_name"] = "updated", ["val"] = 99 };

        marshaller.ApplyDtoProperties(source, dto, typeof(DtoWithJsonPropertyName));

        Assert.Equal("updated", dto.DisplayName);
        Assert.Equal(99, dto.Value);
    }

    [Fact]
    public void ApplyDtoProperties_RespectsJsonPropertyName_IgnoresCamelCaseKey()
    {
        var marshaller = CreateMarshaller();
        var dto = new DtoWithJsonPropertyName { DisplayName = "original", Value = 5 };
        // Use camelCase CLR name instead of the [JsonPropertyName] — should NOT match
        var source = new JsonObject { ["displayName"] = "should-not-apply" };

        marshaller.ApplyDtoProperties(source, dto, typeof(DtoWithJsonPropertyName));

        Assert.Equal("original", dto.DisplayName);
    }

    [Fact]
    public void ApplyDtoProperties_RespectsJsonIgnore()
    {
        var marshaller = CreateMarshaller();
        var dto = new DtoWithJsonIgnore { Name = "original", Secret = "keep-me" };
        var source = new JsonObject { ["name"] = "updated", ["secret"] = "should-be-ignored" };

        marshaller.ApplyDtoProperties(source, dto, typeof(DtoWithJsonIgnore));

        Assert.Equal("updated", dto.Name);
        Assert.Equal("keep-me", dto.Secret); // JsonIgnore property unchanged
    }

    [Fact]
    public void ApplyDtoProperties_SkipsReadOnlyProperties()
    {
        var marshaller = CreateMarshaller();
        var dto = new DtoWithReadOnlyProperty { Name = "original" };
        var source = new JsonObject { ["name"] = "updated", ["computed"] = "should-be-ignored" };

        marshaller.ApplyDtoProperties(source, dto, typeof(DtoWithReadOnlyProperty));

        Assert.Equal("updated", dto.Name);
        Assert.Equal("read-only", dto.Computed); // Read-only property unchanged
    }

    [Fact]
    public void ApplyDtoProperties_SkipsIncompatibleJsonValues()
    {
        var marshaller = CreateMarshaller();
        var dto = new TestDto { Name = "original", Count = 10 };
        // Send a string for an int property — should be silently skipped
        var source = new JsonObject { ["count"] = "not-a-number" };

        marshaller.ApplyDtoProperties(source, dto, typeof(TestDto));

        Assert.Equal("original", dto.Name);
        Assert.Equal(10, dto.Count); // Count unchanged due to incompatible value
    }

    [Fact]
    public void IsDtoType_ReturnsTrueForRegisteredDtoType()
    {
        var marshaller = CreateMarshaller();

        Assert.True(marshaller.IsDtoType(typeof(TestDto)));
    }

    [Fact]
    public void IsDtoType_ReturnsFalseForNonDtoType()
    {
        var marshaller = CreateMarshaller();

        Assert.False(marshaller.IsDtoType(typeof(string)));
        Assert.False(marshaller.IsDtoType(typeof(TestClass)));
    }

    private enum TestEnum
    {
        ValueA,
        ValueB
    }

    private sealed class TestClass
    {
        public int Value { get; set; }
    }

    [AspireDto]
    private sealed class TestDto
    {
        public string? Name { get; set; }
        public int Count { get; set; }
    }

    [AspireDto]
    private sealed class TestDtoWithEnum
    {
        public string? Label { get; set; }
        public TestEnum Status { get; set; }
    }

    [AspireDto]
    private sealed class SelfReferencingDto
    {
        public string? Name { get; set; }
        public SelfReferencingDto? Self { get; set; }
    }

    [AspireDto]
    private sealed class ParentDto
    {
        public string? Label { get; set; }
        public ChildDto? Child { get; set; }
    }

    [AspireDto]
    private sealed class ChildDto
    {
        public string? Label { get; set; }
        public ParentDto? Parent { get; set; }
    }

    [AspireDto]
    private sealed class DtoWithComplexProperty
    {
        public string? Name { get; set; }
        public NonDeserializableType Complex { get; set; } = new("default");
    }

    /// <summary>
    /// A type that cannot be deserialized by System.Text.Json (multiple parameterized constructors, none annotated).
    /// Simulates EndpointReference in ResourceUrlAnnotation.
    /// </summary>
    private sealed class NonDeserializableType
    {
        public string Value { get; }
        public NonDeserializableType(string value) => Value = value;
        public NonDeserializableType(string value, int extra) => Value = value + extra;
    }

    [AspireDto]
    private sealed class DtoWithJsonPropertyName
    {
        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("val")]
        public int Value { get; set; }
    }

    [AspireDto]
    private sealed class DtoWithJsonIgnore
    {
        public string? Name { get; set; }

        [JsonIgnore]
        public string? Secret { get; set; }
    }

    [AspireDto]
    private sealed class DtoWithReadOnlyProperty
    {
        public string? Name { get; set; }
        public string Computed { get; } = "read-only";
    }

    [Fact]
    public void MarshalToJson_MarshalsConditionalReferenceExpressionAsHandle()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var condition = new TestConditionValueProvider(bool.TrueString);
        var whenTrue = ReferenceExpression.Create($",ssl=true");
        var whenFalse = ReferenceExpression.Empty;
        var conditional = ReferenceExpression.CreateConditional(condition, bool.TrueString, whenTrue, whenFalse);

        var result = marshaller.MarshalToJson(conditional);

        Assert.NotNull(result);
        Assert.IsType<JsonObject>(result);
        var jsonObj = (JsonObject)result;
        Assert.NotNull(jsonObj["$handle"]);
        Assert.NotNull(jsonObj["$type"]);
    }

    [Fact]
    public void MarshalToJson_ConditionalReferenceExpression_RoundTripsViaHandle()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var condition = new TestConditionValueProvider(bool.TrueString);
        var whenTrue = ReferenceExpression.Create($",ssl=true");
        var whenFalse = ReferenceExpression.Empty;
        var conditional = ReferenceExpression.CreateConditional(condition, bool.TrueString, whenTrue, whenFalse);

        var json = marshaller.MarshalToJson(conditional);
        Assert.NotNull(json);

        var handleId = json["$handle"]!.GetValue<string>();
        var found = registry.TryGet(handleId, out var retrieved, out _);

        Assert.True(found);
        Assert.Same(conditional, retrieved);
    }

    [Fact]
    public async Task MarshalToJson_ConditionalReferenceExpression_PreservesValueAfterRoundTrip()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var condition = new TestConditionValueProvider(bool.TrueString);
        var whenTrue = ReferenceExpression.Create($",ssl=true");
        var whenFalse = ReferenceExpression.Empty;
        var conditional = ReferenceExpression.CreateConditional(condition, bool.TrueString, whenTrue, whenFalse);

        var json = marshaller.MarshalToJson(conditional);
        var handleId = json!["$handle"]!.GetValue<string>();
        registry.TryGet(handleId, out var retrieved, out _);
        var retrievedConditional = Assert.IsType<ReferenceExpression>(retrieved);

        Assert.StartsWith("cond-test-condition", retrievedConditional.Name);
        Assert.Equal(",ssl=true", await retrievedConditional.GetValueAsync(default));
    }

    [Fact]
    public async Task MarshalToJson_ConditionalReferenceExpression_FalseConditionRoundTrips()
    {
        var registry = new HandleRegistry();
        var marshaller = CreateMarshaller(registry);
        var condition = new TestConditionValueProvider(bool.FalseString);
        var whenTrue = ReferenceExpression.Create($",ssl=true");
        var whenFalse = ReferenceExpression.Empty;
        var conditional = ReferenceExpression.CreateConditional(condition, bool.TrueString, whenTrue, whenFalse);

        var json = marshaller.MarshalToJson(conditional);
        var handleId = json!["$handle"]!.GetValue<string>();
        registry.TryGet(handleId, out var retrieved, out _);
        var retrievedConditional = Assert.IsType<ReferenceExpression>(retrieved);

        Assert.Null(await retrievedConditional.GetValueAsync(default));
    }

    private sealed class TestConditionValueProvider(string value) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => "test-condition";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => new(value);

        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
            => new(value);
    }

    [Fact]
    public void UnmarshalFromJson_UnmarshalsCondExprToReferenceExpression()
    {
        var registry = new HandleRegistry();

        // Register a condition IValueProvider as a handle
        var condition = new TestConditionValueProvider(bool.TrueString);
        var conditionHandleId = registry.Register(condition, AtsConstants.ReferenceExpressionTypeId);

        var (marshaller, context) = CreateMarshallerWithContext(registry);

        // Build the unified $expr JSON with conditional mode
        var json = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["condition"] = new JsonObject
                {
                    ["$handle"] = conditionHandleId
                },
                ["whenTrue"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ",ssl=true"
                    }
                },
                ["whenFalse"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ""
                    }
                }
            }
        };

        var result = marshaller.UnmarshalFromJson(json, typeof(ReferenceExpression), context);
        var cre = Assert.IsType<ReferenceExpression>(result);

        Assert.NotNull(cre);
    }

    [Fact]
    public async Task UnmarshalFromJson_CondExpr_TrueConditionReturnsWhenTrueValue()
    {
        var registry = new HandleRegistry();

        var condition = new TestConditionValueProvider(bool.TrueString);
        var conditionHandleId = registry.Register(condition, AtsConstants.ReferenceExpressionTypeId);

        var (marshaller, context) = CreateMarshallerWithContext(registry);

        var json = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["condition"] = new JsonObject
                {
                    ["$handle"] = conditionHandleId
                },
                ["whenTrue"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ",ssl=true"
                    }
                },
                ["whenFalse"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ""
                    }
                }
            }
        };

        var result = marshaller.UnmarshalFromJson(json, typeof(ReferenceExpression), context);
        var cre = Assert.IsType<ReferenceExpression>(result);
        var value = await cre.GetValueAsync(default);

        Assert.Equal(",ssl=true", value);
    }

    [Fact]
    public async Task UnmarshalFromJson_CondExpr_FalseConditionReturnsWhenFalseValue()
    {
        var registry = new HandleRegistry();

        var condition = new TestConditionValueProvider(bool.FalseString);
        var conditionHandleId = registry.Register(condition, AtsConstants.ReferenceExpressionTypeId);

        var (marshaller, context) = CreateMarshallerWithContext(registry);

        var json = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["condition"] = new JsonObject
                {
                    ["$handle"] = conditionHandleId
                },
                ["whenTrue"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ",ssl=true"
                    }
                },
                ["whenFalse"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ""
                    }
                }
            }
        };

        var result = marshaller.UnmarshalFromJson(json, typeof(ReferenceExpression), context);
        var cre = Assert.IsType<ReferenceExpression>(result);
        var value = await cre.GetValueAsync(default);

        // An empty format string with no value providers results in null from ReferenceExpression.GetValueAsync
        Assert.Null(value);
    }

    [Fact]
    public void UnmarshalFromJson_CondExpr_ThrowsWhenConditionHandleMissing()
    {
        var registry = new HandleRegistry();
        var (marshaller, context) = CreateMarshallerWithContext(registry);

        var json = new JsonObject
        {
            ["$expr"] = new JsonObject
            {
                ["condition"] = new JsonObject
                {
                    ["$handle"] = "nonexistent-handle-id"
                },
                ["whenTrue"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ",ssl=true"
                    }
                },
                ["whenFalse"] = new JsonObject
                {
                    ["$expr"] = new JsonObject
                    {
                        ["format"] = ""
                    }
                }
            }
        };

        Assert.Throws<CapabilityException>(() =>
            marshaller.UnmarshalFromJson(json, typeof(ReferenceExpression), context));
    }
}
