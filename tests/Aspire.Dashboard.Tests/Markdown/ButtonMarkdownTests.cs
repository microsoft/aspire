// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Xunit;

namespace Aspire.Dashboard.Tests.Markdown;

public class ButtonMarkdownTests
{
    [Fact]
    public void ButtonConfig_ParseInline_AllProperties()
    {
        var content = "type=button action=doSomething arguments=id=123&name=test icon=Send";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("doSomething", config.Values["action"]);
        Assert.Equal("id=123&name=test", config.Values["arguments"]);
        Assert.Equal("Send", config.Icon);
        Assert.DoesNotContain(config.Values, kvp => kvp.Key == "type");
    }

    [Fact]
    public void ButtonConfig_ParseInline_ArgumentsWithMultipleEquals()
    {
        var content = "type=button action=echo arguments=message=Hello+World&count=42&flag=true";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("echo", config.Values["action"]);
        Assert.Equal("message=Hello+World&count=42&flag=true", config.Values["arguments"]);
    }

    [Fact]
    public void ButtonConfig_ParseInline_ActionOnly()
    {
        var content = "type=button action=myAction";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("myAction", config.Values["action"]);
        Assert.Null(config.Icon);
        Assert.DoesNotContain(config.Values, kvp => kvp.Key.Equals("arguments", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ButtonConfig_ParseInline_CaseInsensitiveKeys()
    {
        var content = "TYPE=button ACTION=download ICON=Download ARGUMENTS=id=1";

        var config = ButtonConfig.ParseInline(content);

        Assert.Equal("download", config.Values["action"]);
        Assert.Equal("Download", config.Icon);
        Assert.Equal("id=1", config.Values["arguments"]);
    }

    [Fact]
    public void ButtonConfig_ParseInline_TypeKeySkipped()
    {
        var content = "type=button action=test";

        var config = ButtonConfig.ParseInline(content);

        Assert.DoesNotContain(config.Values, kvp => kvp.Key.Equals("type", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("test", config.Values["action"]);
    }

    [Fact]
    public void ButtonMarkdown_RendersButton()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Click Me](type=button action=doSomething icon=Send)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-text=\"Click Me\"", html);
        Assert.Contains("data-action=\"doSomething\"", html);
        Assert.Contains("Click Me", html);
    }

    [Fact]
    public void ButtonMarkdown_WithArguments()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Delete](type=button action=delete-todo arguments=id=42)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-action=\"delete-todo\"", html);
        Assert.Contains("data-arguments=\"id=42\"", html);
        Assert.Contains("Delete", html);
    }

    [Fact]
    public void ButtonMarkdown_InlineInParagraph()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "Click [here](type=button action=navigate) to proceed.";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("to proceed", html);
    }

    [Fact]
    public void ButtonMarkdown_RegularLinksNotAffected()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Google](https://google.com)";

        var html = processor.ToHtml(markdown);

        Assert.DoesNotContain("<fluent-button", html);
        Assert.Contains("<a", html);
        Assert.Contains("https://google.com", html);
    }

    [Fact]
    public void ButtonMarkdown_ComplexArguments()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Echo](type=button action=echo-args arguments=message=Hello+from+button&repeat=3&shout=true)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-action=\"echo-args\"", html);
        Assert.Contains("data-arguments=\"message=Hello+from+button&amp;repeat=3&amp;shout=true\"", html);
    }

    [Fact]
    public void ButtonMarkdown_MultipleButtons()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Save](type=button action=save) [Cancel](type=button action=cancel)";

        var html = processor.ToHtml(markdown);

        var buttonCount = System.Text.RegularExpressions.Regex.Matches(html, "<fluent-button").Count;
        Assert.Equal(2, buttonCount);
        Assert.Contains("data-action=\"save\"", html);
        Assert.Contains("data-action=\"cancel\"", html);
    }

    [Fact]
    public void ButtonMarkdown_WithoutIcon_NoSvg()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Submit](type=button action=submit_form)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("Submit", html);
        Assert.DoesNotContain("<svg slot=\"start\"", html);
    }

    [Fact]
    public void ButtonMarkdown_EmptyText_WithIcon_RendersIconOnly()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[](type=button action=delete icon=Delete)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("<fluent-button", html);
        Assert.Contains("data-action=\"delete\"", html);
        Assert.Contains("aria-label=\"Delete\"", html);
    }

    [Fact]
    public void ButtonMarkdown_EmptyText_NoIcon_NotRendered()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[](type=button action=click)";

        var html = processor.ToHtml(markdown);

        Assert.DoesNotContain("<fluent-button", html);
    }

    [Fact]
    public void ButtonMarkdown_MissingClosingParen_NotParsed()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Click](type=button action=test";

        var html = processor.ToHtml(markdown);

        Assert.DoesNotContain("<fluent-button", html);
    }

    [Fact]
    public void ButtonMarkdown_SpecialCharactersInText()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Download & Share](type=button action=download)";

        var html = processor.ToHtml(markdown);

        Assert.Contains("Download &amp; Share", html);
    }

    [Fact]
    public void ButtonMarkdown_LinkWithoutTypeButton_NotAButton()
    {
        var processor = CreateMarkdownProcessor();
        var markdown = "[Click](action=test)";

        var html = processor.ToHtml(markdown);

        // Without type=button prefix, it should not be treated as a button
        Assert.DoesNotContain("<fluent-button", html);
    }

    internal static MarkdownProcessor CreateMarkdownProcessor()
    {
        return new MarkdownProcessor(
            new TestStringLocalizer<ControlsStrings>(),
            safeUrlSchemes: null,
            extensions: [new ButtonExtension()]);
    }
}
