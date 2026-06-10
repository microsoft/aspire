// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Utils;

// Based on tests at https://github.com/distribution/reference/blob/main/reference_test.go
[Trait("Partition", "4")]
public class ContainerReferenceParserTests
{
    [Fact]
    public void EmptyInput()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ContainerReferenceParser.Parse(""));
        Assert.StartsWith("repository name must have at least one component", ex.Message);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData(":justtag")]
    [InlineData("@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("aa/asdf$$^/aa")]
    [InlineData("[2001:db8::1]")]
    [InlineData("[2001:db8::1]:5000")]
    [InlineData("[2001:db8::1]:tag")]
    [InlineData("[fe80::1%eth0]:5000/repo")]
    [InlineData("[fe80::1%@invalidzone]:5000/repo")]
    public void InvalidReferenceFormat(string input)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ContainerReferenceParser.Parse(input));
        Assert.StartsWith("invalid reference format", ex.Message);
    }

    [Theory]
    [InlineData("test_com", "test_com")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    public void ImageTests(string input, string expectedImage)
        => ParserTest(input, null, expectedImage, null, null);

    [Theory]
    [InlineData("test_com:tag", "test_com", "tag")]
    [InlineData("test.com:5000", "test.com", "5000")]
    [InlineData("lowercase:Uppercase", "lowercase", "Uppercase")]
    [InlineData("foo_bar.com:8080", "foo_bar.com", "8080")]
    [InlineData("192.168.1.1:tag", "192.168.1.1", "tag")]
    [InlineData("192.168.1.1:5000", "192.168.1.1", "5000")]
    [InlineData("a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a:tag-puts-this-over-max", "a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a/a", "tag-puts-this-over-max")]
    [InlineData("foo/foo_bar.com:8080", "foo/foo_bar.com", "8080")]
    public void ImageAndTagTests(string input, string expectedImage, string expectedTag)
        => ParserTest(input, null, expectedImage, expectedTag: expectedTag, null);

    [Theory]
    [InlineData("test:5000/repo", "test:5000", "repo")]
    [InlineData("sub-dom1.foo.com/bar/baz/quux", "sub-dom1.foo.com", "bar/baz/quux")]
    [InlineData("192.168.1.1/repo", "192.168.1.1", "repo")]
    [InlineData("192.168.1.1:5000/repo", "192.168.1.1:5000", "repo")]
    [InlineData("[2001:db8::1]/repo", "[2001:db8::1]", "repo")]
    [InlineData("[2001:db8::1]:5000/repo", "[2001:db8::1]:5000", "repo")]
    [InlineData("[2001:db8::]:5000/repo", "[2001:db8::]:5000", "repo")]
    [InlineData("[::1]:5000/repo", "[::1]:5000", "repo")]
    public void DomainAndImageTests(string input, string expectedRegistry, string expectedImage)
        => ParserTest(input, expectedRegistry, expectedImage, null, null);

    [Theory]
    [InlineData("test.com/repo:tag", "test.com", "repo", "tag")]
    [InlineData("test:5000/repo:tag", "test:5000", "repo", "tag")]
    [InlineData("sub-dom1.foo.com/bar/baz/quux:some-long-tag", "sub-dom1.foo.com", "bar/baz/quux", "some-long-tag")]
    [InlineData("b.gcr.io/test.example.com/my-app:test.example.com", "b.gcr.io", "test.example.com/my-app", "test.example.com")]
    [InlineData("xn--n3h.com/myimage:xn--n3h.com", "xn--n3h.com", "myimage", "xn--n3h.com")] // ☃.com in punycode
    [InlineData("192.168.1.1:5000/repo:5050", "192.168.1.1:5000", "repo", "5050")]
    [InlineData("[2001:db8:1:2:3:4:5:6]/repo:tag", "[2001:db8:1:2:3:4:5:6]", "repo", "tag")]
    [InlineData("[2001:db8::1]:5000/repo:tag", "[2001:db8::1]:5000", "repo", "tag")]
    [InlineData("localhost/repo:tag", "localhost", "repo", "tag")]
    public void DomainImageAndTagTests(string input, string expectedRegistry, string expectedImage, string expectedTag)
        => ParserTest(input, expectedRegistry, expectedImage, expectedTag, null);

    [Theory]
    [InlineData("test:5000/repo@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "test:5000", "repo", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("[2001:db8::1]:5000/repo@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "[2001:db8::1]:5000", "repo", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("whatever:5000/repo@algo:value", "whatever:5000", "repo", "algo:value")]
    [InlineData("localhost/repo@digest", "localhost", "repo", "digest")]
    public void DomainImageAndDigestTests(string input, string expectedRegistry, string expectedImage, string expectedDigest)
        => ParserTest(input, expectedRegistry, expectedImage, null, expectedDigest);

    [Theory]
    [InlineData("test:5000/repo:tag@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "test:5000", "repo", "tag", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("xn--7o8h.com/myimage:xn--7o8h.com@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "xn--7o8h.com", "myimage", "xn--7o8h.com", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")] // 🐳.com in punycode
    [InlineData("[2001:db8::1]:5000/repo:tag@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "[2001:db8::1]:5000", "repo", "tag", "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void DomainImageTagAndDigestTests(string input, string expectedRegistry, string expectedImage, string expectedTag, string expectedDigest)
        => ParserTest(input, expectedRegistry, expectedImage, expectedTag, expectedDigest);

    private static void ParserTest(string input, string? expectedRegistry, string expectedImage, string? expectedTag, string? expectedDigest)
    {
        var result = ContainerReferenceParser.Parse(input);

        Assert.Multiple(() =>
        {
            Assert.Equal(expectedRegistry, result.Registry);
            Assert.Equal(expectedImage, result.Image);
            Assert.Equal(expectedTag, result.Tag);
            Assert.Equal(expectedDigest, result.Digest);
        });

    }

}
