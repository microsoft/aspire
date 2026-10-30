// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Documentation.ApiDocs;

namespace Aspire.Cli.Tests.Documentation.ApiDocs;

public class ApiMemberMarkdownParserTests
{
    [Fact]
    public void Parse_UsesKnownMemberGroupRoutes()
    {
        var containerItem = new ApiReferenceItem
        {
            Id = "csharp/aspire.test.package/testresource",
            Name = "TestResource",
            Language = ApiReferenceLanguages.CSharp,
            Kind = ApiReferenceKinds.Type,
            PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testresource"
        };

        var memberGroupItem = new ApiReferenceItem
        {
            Id = "csharp/aspire.test.package/testresource/methods",
            Name = "methods",
            Language = ApiReferenceLanguages.CSharp,
            Kind = ApiReferenceKinds.MemberGroup,
            PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testresource/methods",
            ParentId = containerItem.Id,
            MemberGroup = "methods"
        };

        var items = ApiMemberMarkdownParser.Parse(
            containerItem,
            """
            # TestResource

            ## Methods

            - [RunEmulator()](/reference/api/csharp/aspire.test.package/testresource/methods.md#runemulator) : `void` -- Runs the emulator.
            - [OtherType](/reference/api/csharp/aspire.test.package/othertype.md) -- Should not be treated as a member.
            """,
            ApiDocsSourceConfiguration.DefaultSitemapUrl,
            new Dictionary<string, ApiReferenceItem>(StringComparer.OrdinalIgnoreCase)
            {
                [memberGroupItem.PageUrl] = memberGroupItem
            });

        var item = Assert.Single(items);
        Assert.Equal("csharp/aspire.test.package/testresource/methods#runemulator", item.Id);
        Assert.Equal("RunEmulator()", item.Name);
        Assert.Equal(ApiReferenceLanguages.CSharp, item.Language);
        Assert.Equal(ApiReferenceKinds.Member, item.Kind);
        Assert.Equal(memberGroupItem.Id, item.ParentId);
        Assert.Equal("Runs the emulator.", item.Summary);
    }

    [Fact]
    public void Parse_ShortenOverloadIdsWhileKeepingThemDistinct()
    {
        var containerItem = new ApiReferenceItem
        {
            Id = "csharp/aspire.test.package/resourcebuilderextensions",
            Name = "ResourceBuilderExtensions",
            Language = ApiReferenceLanguages.CSharp,
            Kind = ApiReferenceKinds.Type,
            PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/resourcebuilderextensions"
        };

        var memberGroupItem = new ApiReferenceItem
        {
            Id = "csharp/aspire.test.package/resourcebuilderextensions/methods",
            Name = "methods",
            Language = ApiReferenceLanguages.CSharp,
            Kind = ApiReferenceKinds.MemberGroup,
            PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods",
            ParentId = containerItem.Id,
            MemberGroup = "methods"
        };

        var items = ApiMemberMarkdownParser.Parse(
            containerItem,
            """
            # ResourceBuilderExtensions

            ## Methods

            - [WithEnvironment(IResourceBuilder<T>, string, string?)](/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods.md#withenvironment-iresourcebuilder-t-string-string) : `IResourceBuilder<T>` -- Adds a string environment variable.
            - [WithEnvironment(IResourceBuilder<T>, Action<EnvironmentCallbackContext>)](/reference/api/csharp/aspire.test.package/resourcebuilderextensions/methods.md#withenvironment-iresourcebuilder-t-action-environmentcallbackcontext) : `IResourceBuilder<T>` -- Adds environment variables from a callback.
            """,
            ApiDocsSourceConfiguration.DefaultSitemapUrl,
            new Dictionary<string, ApiReferenceItem>(StringComparer.OrdinalIgnoreCase)
            {
                [memberGroupItem.PageUrl] = memberGroupItem
            });

        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            Assert.True(item.Id.StartsWith("csharp/aspire.test.package/resourcebuilderextensions/methods#withenvironment-", StringComparison.Ordinal), item.Id);
            Assert.DoesNotContain("iresourcebuilder", item.Id, StringComparison.OrdinalIgnoreCase);
        });
        Assert.NotEqual(items[0].Id, items[1].Id);
    }

    [Fact]
    public void Parse_IgnoresUnknownSectionsAndFencedCodeAroundMemberLinks()
    {
        // Only C# pages carry grouped member bullets, so the Go-shaped sections are mixed into a
        // C# page to prove that unrecognized sections and fenced code don't disturb extraction.
        var containerItem = new ApiReferenceItem
        {
            Id = "csharp/aspire.test.package/testresource",
            Name = "TestResource",
            Language = ApiReferenceLanguages.CSharp,
            Kind = ApiReferenceKinds.Type,
            PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testresource"
        };

        var items = ApiMemberMarkdownParser.Parse(
            containerItem,
            """
            # TestResource

            ## Definition

            ```go
            func (r *testResource) RunEmulator() error
            ```

            ## Methods

            - [RunEmulator()](/reference/api/csharp/aspire.test.package/testresource/methods.md#runemulator) : `void` -- Runs the emulator.

            ## Options

            - `Port` (`*float64`) -- The host port to bind.

            ## Errors

            Builder methods defer failures. Call `Err()` on the returned handle to observe them.
            """,
            ApiDocsSourceConfiguration.DefaultSitemapUrl,
            CreateCSharpMemberGroups());

        var item = Assert.Single(items);
        Assert.Equal("csharp/aspire.test.package/testresource/methods#runemulator", item.Id);
        Assert.Equal("RunEmulator()", item.Name);
        Assert.Equal("Runs the emulator.", item.Summary);
    }

    // Go method lists use the same "- [Name](link)" shape as C# member overview lines. Only the known
    // grouped member pages decide whether a link becomes a member, and Go has no grouped member pages.
    [Fact]
    public void Parse_ReturnsNoMembersForGoItemPageWhenNoGoMemberGroupsAreKnown()
    {
        var containerItem = new ApiReferenceItem
        {
            Id = "go/aspire.hosting/containerresource",
            Name = "containerresource",
            Language = ApiReferenceLanguages.Go,
            Kind = ApiReferenceKinds.Symbol,
            PageUrl = "https://aspire.dev/reference/api/go/aspire.hosting/containerresource",
            ParentId = "go/aspire.hosting"
        };

        var items = ApiMemberMarkdownParser.Parse(
            containerItem,
            """
            # ContainerResource

            - Module: `aspire.hosting`
            - Kind: `interface`

            Represents a container resource.

            ## Definition

            ```go
            type ContainerResource interface {
                handleReference
                WithEndpoint(options ...*WithEndpointOptions) ContainerResource
                WithEnvironment(name string, value any) ContainerResource
                Err() error
            }
            ```

            ## Methods

            - [WithEndpoint](/reference/api/go/aspire.hosting/containerresource/withendpoint.md) -- `method` -- Adds a network endpoint.
            - [WithEnvironment](/reference/api/go/aspire.hosting/containerresource/withenvironment.md) -- `method` -- Sets an environment variable.

            ## Errors

            Builder methods defer failures. Call `Err()` on the returned handle to observe them.

            ## Applies to

            - [ContainerResource](/reference/api/go/aspire.hosting/containerresource.md)
            """,
            ApiDocsSourceConfiguration.DefaultSitemapUrl,
            CreateCSharpMemberGroups());

        Assert.Empty(items);
    }

    [Fact]
    public void Parse_ReturnsNoMembersForGoMemberPageWhenNoGoMemberGroupsAreKnown()
    {
        var containerItem = new ApiReferenceItem
        {
            Id = "go/aspire.hosting/containerresource/withendpoint",
            Name = "withendpoint",
            Language = ApiReferenceLanguages.Go,
            Kind = ApiReferenceKinds.Member,
            PageUrl = "https://aspire.dev/reference/api/go/aspire.hosting/containerresource/withendpoint",
            ParentId = "go/aspire.hosting/containerresource"
        };

        var items = ApiMemberMarkdownParser.Parse(
            containerItem,
            """
            # WithEndpoint

            Adds a network endpoint to the container resource.

            ## Definition

            ```go
            func (r *containerResource) WithEndpoint(options ...*WithEndpointOptions) ContainerResource
            ```

            ## Signature

            ```go
            WithEndpoint(options ...*WithEndpointOptions) ContainerResource
            ```

            ## Parameters

            - `options` (`...*WithEndpointOptions`) -- Optional parameters. See Options.

            ## Options

            - `Port` (`*float64`) -- The host port to bind.
            - `Name` (`*string`) -- The endpoint name.

            ## Returns

            - [ContainerResource](/reference/api/go/aspire.hosting/containerresource.md) -- The receiver handle. Failures are deferred, so check `Err()` for the `error`.

            ## Defined on

            - [ContainerResource](/reference/api/go/aspire.hosting/containerresource.md)
            """,
            ApiDocsSourceConfiguration.DefaultSitemapUrl,
            CreateCSharpMemberGroups());

        Assert.Empty(items);
    }

    // The grouped member pages are C# only. A non-empty lookup keeps Parse from returning early,
    // so the markdown is actually scanned.
    private static Dictionary<string, ApiReferenceItem> CreateCSharpMemberGroups()
    {
        var memberGroupItem = new ApiReferenceItem
        {
            Id = "csharp/aspire.test.package/testresource/methods",
            Name = "methods",
            Language = ApiReferenceLanguages.CSharp,
            Kind = ApiReferenceKinds.MemberGroup,
            PageUrl = "https://aspire.dev/reference/api/csharp/aspire.test.package/testresource/methods",
            ParentId = "csharp/aspire.test.package/testresource",
            MemberGroup = "methods"
        };

        return new Dictionary<string, ApiReferenceItem>(StringComparer.OrdinalIgnoreCase)
        {
            [memberGroupItem.PageUrl] = memberGroupItem
        };
    }
}
