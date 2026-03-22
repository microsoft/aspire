// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class StableConnectionStringBuilderTests
{
    [Fact]
    public void Parses_ConnectionString_And_Preserves_Order()
    {
        var cs = "Key1=Value1;Key2=Value2;Key3=Value3";
        var builder = new StableConnectionStringBuilder(cs);

        Assert.Equal("Value1", builder["Key1"]);
        Assert.Equal("Value2", builder["Key2"]);
        Assert.Equal("Value3", builder["Key3"]);
        Assert.Equal(cs, builder.ToString());
    }

    [Fact]
    public void Parses_ConnectionString_With_Base64()
    {
        var cs = "Key1=ABC=;Key2=ABC==;Key3===";
        var builder = new StableConnectionStringBuilder(cs);

        Assert.Equal("ABC=", builder["Key1"]);
        Assert.Equal("ABC==", builder["Key2"]);
        Assert.Equal("==", builder["Key3"]);
    }

    [Fact]
    public void Indexer_Can_Set_And_Add_Values()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2");
        builder["B"] = "22";
        builder["C"] = "3";

        Assert.Equal("1", builder["A"]);
        Assert.Equal("22", builder["B"]);
        Assert.Equal("3", builder["C"]);
        Assert.Equal("A=1;B=22;C=3;", builder.ToString());
    }

    [Fact]
    public void TryGetValue_Is_CaseInsensitive_And_Does_Not_Change_Key_Case()
    {
        var builder = new StableConnectionStringBuilder("Foo=Bar");
        Assert.True(builder.TryGetValue("foo", out var value));
        Assert.Equal("Bar", value);
        builder["FOO"] = "Baz";
        Assert.Equal("Baz", builder["foo"]);
        Assert.Equal("Foo=Baz", builder.ToString());
    }

    [Fact]
    public void Adding_New_Key_Preserves_Order()
    {
        var builder = new StableConnectionStringBuilder("X=1;Y=2");
        builder["Z"] = "3";
        Assert.Equal("X=1;Y=2;Z=3;", builder.ToString());
    }

    [Fact]
    public void Indexer_Returns_Empty_String_For_Empty_Value_And_Null_For_Missing_Key()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=;C=3");

        // Key exists, value is empty
        Assert.Equal(string.Empty, builder["B"]);

        // Key exists, value is not empty
        Assert.Equal("1", builder["A"]);
        Assert.Equal("3", builder["C"]);

        // Key does not exist
        Assert.Null(builder["D"]);
    }

    [Fact]
    public void Indexer_Removes_Key_When_Setting_Value_To_Null()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;C=3");
        builder["B"] = null;

        // Key "B" should be removed
        Assert.Null(builder["B"]);
        Assert.Equal("A=1;C=3", builder.ToString());

        // Setting a non-existent key to null should not throw and should not add the key
        builder["D"] = null;
        Assert.Null(builder["D"]);
        Assert.Equal("A=1;C=3", builder.ToString());
    }

    [Fact]
    public void Successive_Semicolons_Are_Preserved_When_Serializing()
    {
        var original = "A=1;;B=2;;;C=3;";
        var builder = new StableConnectionStringBuilder(original);

        // The ToString() result should preserve the original placement of successive semicolons
        Assert.Equal(original, builder.ToString());
    }

    [Theory]
    [InlineData("Key1=Value1;Key2=Value2")]
    [InlineData("Key1=Value1;Key2=Value2;")]
    [InlineData("Key1=Value1;Key2=Value2;;")]
    [InlineData("Key1=Value1;Key2=Value2;;;")]
    public void ToString_PreservesMultipleTrailingSemicolons(string connectionString)
    {
        var builder = new StableConnectionStringBuilder(connectionString);
        Assert.Equal(connectionString, builder.ToString());
    }

    [Fact]
    public void Removing_Key_Removes_Trailing_Semicolon()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;");
        builder["B"] = null;
        Assert.Equal("A=1;", builder.ToString());
    }

    [Fact]
    public void Removing_Last_Key_Removes_Only_One_Trailing_Semicolon()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;");
        builder["B"] = null;
        Assert.Equal("A=1;", builder.ToString());
    }

    [Fact]
    public void Removing_Middle_Key_Removes_Only_Its_Semicolon()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;;C=3;");
        builder["B"] = null;
        Assert.Equal("A=1;;C=3;", builder.ToString());
    }

    [Theory]
    [InlineData("A=1;B=2;A=3")]
    [InlineData("A=1;B=2;a=3")]
    [InlineData("A=1;A=2;A=3")]
    [InlineData("A=1;A=2;B=3")]
    [InlineData("A=1;A=2;B=3;b=3;")]
    [InlineData("A=1; A =2")]
    public void Throws_On_Duplicate_Keys(string connectionString)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var builder = new StableConnectionStringBuilder(connectionString);
        });
        Assert.Contains("Duplicate key", ex.Message);
    }

    [Theory]
    [InlineData("A=1;B;C=2")]
    [InlineData("B;A=1;C=2")]
    [InlineData("A=1;C=2;B")]
    public void Throws_On_Invalid_Segment(string connectionString)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
        {
            var builder = new StableConnectionStringBuilder(connectionString);
        });
        Assert.Contains("Invalid segment", ex.Message);
    }

    [Fact]
    public void Remove_Removes_Key_And_Semicolon()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;C=3;");
        var result = builder.Remove("B");
        Assert.True(result);
        Assert.Equal("A=1;C=3;", builder.ToString());
        Assert.Null(builder["B"]);
    }

    [Fact]
    public void Remove_Returns_False_If_Key_Not_Found()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;C=3;");
        var result = builder.Remove("X");
        Assert.False(result);
        Assert.Equal("A=1;B=2;C=3;", builder.ToString());
    }

    [Fact]
    public void Remove_Removes_Only_One_Semicolon()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;;C=3;");
        builder.Remove("B");
        Assert.Equal("A=1;;C=3;", builder.ToString());
    }

    [Fact]
    public void Remove_Last_Key_Removes_Only_One_Trailing_Semicolon()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;");
        builder.Remove("B");
        Assert.Equal("A=1;", builder.ToString());
    }

    [Fact]
    public void IEnumerable_Enumerates_KeyValuePairs_In_Order()
    {
        var builder = new StableConnectionStringBuilder("A=1;B=2;C=3;");
        var expected = new[]
        {
            new KeyValuePair<string, string>("A", "1"),
            new KeyValuePair<string, string>("B", "2"),
            new KeyValuePair<string, string>("C", "3"),
        };

        var actual = builder.ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParse_Returns_True_For_Valid_ConnectionString()
    {
        var valid = "A=1;B=2;C=3";
        var result = StableConnectionStringBuilder.TryParse(valid, out var builder);
        Assert.True(result);
        Assert.NotNull(builder);
        Assert.Equal("1", builder!["A"]);
        Assert.Equal("2", builder!["B"]);
        Assert.Equal("3", builder!["C"]);
    }

    [Fact]
    public void TryParse_Returns_False_For_Invalid_ConnectionString()
    {
        var invalid = "A=1;B;C=3";
        var result = StableConnectionStringBuilder.TryParse(invalid, out var builder);
        Assert.False(result);
        Assert.Null(builder);
    }

    [Theory]
    [InlineData("A= 1 ;B=2 ;")]
    [InlineData("A=1;B=2 ;")]
    [InlineData("A=1 ;B=2")]
    [InlineData("A=1 ;B=2 ")]
    [InlineData(" A =1 ; B =2")]
    public void Spaces_Are_Preserved(string connectionString)
    {
        var builder = new StableConnectionStringBuilder(connectionString);
        Assert.Equal(connectionString, builder.ToString());
    }

    [Theory]
    [InlineData(@"A="""";B=""b""")]
    [InlineData("A==.{}@1=';;B==.{}@1=';;")]
    public void Special_Chars_Are_Preserved(string connectionString)
    {
        var builder = new StableConnectionStringBuilder(connectionString);
        Assert.Equal(connectionString, builder.ToString());
        Assert.True(builder.TryGetValue("A", out _));
        Assert.True(builder.TryGetValue("B", out _));
    }

    [Fact]
    public void Keys_With_Space_Can_Be_Replaced()
    {
        var builder = new StableConnectionStringBuilder(" A =1;B=2");
        builder["A"] = "3 ";
        Assert.Equal(" A =3 ;B=2", builder.ToString());
    }

    [Fact]
    public void Create_With_Empty_ConnectionString()
    {
        var builder = new StableConnectionStringBuilder();
        Assert.Equal("", builder.ToString());
        Assert.Empty(builder.ToList());
        builder["A"] = "1";
        Assert.Equal("A=1;", builder.ToString());
    }

    [Fact]
    public void Spaces_Are_Not_Altered_On_Updates()
    {
        var builder = new StableConnectionStringBuilder(" A = 1 ; B = 2; C = 3;");
        Assert.Equal(" A = 1 ; B = 2; C = 3;", builder.ToString());
        builder["a"] = " 4 ";
        Assert.Equal(" A = 4 ; B = 2; C = 3;", builder.ToString());
    }

    [Fact]
    public void Keys_Are_Trimmed_When_Enumerated()
    {
        var builder = new StableConnectionStringBuilder("  A = 1 ; B = 2; c = 3;");
        var keys = builder.ToList();
        Assert.Equal(3, keys.Count);
        Assert.Equal("A", keys[0].Key);
        Assert.Equal("B", keys[1].Key);
        Assert.Equal("c", keys[2].Key);
    }
}
