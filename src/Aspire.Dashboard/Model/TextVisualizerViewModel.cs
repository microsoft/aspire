// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model;

[DebuggerDisplay("FormatKind = {FormatKind}, Text = {Text}")]
public class TextVisualizerViewModel
{
    public string Text { get; }
    public string FormatKind { get; private set; } = default!;
    public string FormattedText { get; private set; } = default!;
    public List<StringLogLine> Lines { get; set; } = [];
    public List<StringLogLine> FormattedLines { get; set; } = [];

    public TextVisualizerViewModel(string text, bool indentText, string? knownFormat = null, string? fallbackFormat = null)
    {
        Text = text;
        Lines = GetLines(Text, DashboardUIHelpers.PlaintextFormat);

        if (knownFormat != null)
        {
            ChangeFormattedText(knownFormat, indentText ? FormatKnownText(Text, knownFormat) : Text);
        }
        else if (TryFormatJson(Text, out var formattedJson))
        {
            ChangeFormattedText(DashboardUIHelpers.JsonFormat, indentText ? formattedJson : Text);
        }
        else if (TryFormatXml(Text, out var formattedXml))
        {
            ChangeFormattedText(DashboardUIHelpers.XmlFormat, indentText ? formattedXml : Text);
        }
        else
        {
            ChangeFormattedText(fallbackFormat ?? DashboardUIHelpers.PlaintextFormat, Text);
        }
    }

    private static string FormatKnownText(string text, string knownFormat)
    {
        if (knownFormat == DashboardUIHelpers.JsonFormat && TryFormatJson(text, out var formattedJson))
        {
            return formattedJson;
        }

        if (knownFormat == DashboardUIHelpers.XmlFormat && TryFormatXml(text, out var formattedXml))
        {
            return formattedXml;
        }

        return text;
    }

    private static bool TryFormatXml(string text, [NotNullWhen(true)] out string? formattedText)
    {
        // Avoid throwing when reading non-XML by doing a quick check of the first character.
        // This reduces the number of exceptions we throw when reading invalid text and improves performance.
        if (!CouldBeXml(text))
        {
            formattedText = null;
            return false;
        }

        try
        {
            var document = XDocument.Parse(text.Trim());

            using var stringWriter = new StringWriter();
            using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = document.Declaration == null
            });

            document.Save(xmlWriter);
            xmlWriter.Flush();
            formattedText = stringWriter.ToString();
            return true;
        }
        catch (XmlException)
        {
            formattedText = null;
            return false;
        }
    }

    private void ChangeFormattedText(string format, string formattedText)
    {
        FormatKind = format;
        FormattedText = formattedText;
        FormattedLines = GetLines(FormattedText, FormatKind);
    }

    private static List<StringLogLine> GetLines(string text, string formatKind)
    {
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).ToList();

        return lines.Select((line, index) => new StringLogLine(index + 1, line, formatKind)).ToList();
    }

    private static bool TryFormatJson(string text, [NotNullWhen(true)] out string? formattedText)
    {
        // Avoid throwing when reading non-JSON by doing a quick check of the first character.
        // This reduces the number of exceptions we throw when reading invalid text and improves performance.
        if (!CouldBeJson(text))
        {
            formattedText = null;
            return false;
        }

        try
        {
            formattedText = FormatJson(text);
            return true;
        }
        catch (JsonException)
        {
            formattedText = null;
            return false;
        }
    }

    private static string FormatJson(string jsonString)
    {
        var jsonData = Encoding.UTF8.GetBytes(jsonString);
        var rawNumberReplacements = new List<RawNumberReplacement>();

        // Initialize the Utf8JsonReader
        var reader = new Utf8JsonReader(jsonData, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Allow,
            // Increase the allowed limit to 1000. This matches the allowed limit of the writer.
            // It's ok to allow recursion here because JSON is read in a flat loop. There isn't a danger
            // of recursive method calls that cause a stack overflow.
            MaxDepth = 1000
        });

        // Use a MemoryStream and Utf8JsonWriter to write the formatted JSON
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    writer.WriteStartObject();
                    break;
                case JsonTokenType.EndObject:
                    writer.WriteEndObject();
                    break;
                case JsonTokenType.StartArray:
                    writer.WriteStartArray();
                    break;
                case JsonTokenType.EndArray:
                    writer.WriteEndArray();
                    break;
                case JsonTokenType.PropertyName:
                    writer.WritePropertyName(reader.GetString()!);
                    break;
                case JsonTokenType.String:
                    writer.WriteStringValue(reader.GetString());
                    break;
                case JsonTokenType.Number:
                    WriteRawNumberValue(writer, reader, stream, rawNumberReplacements);
                    break;
                case JsonTokenType.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonTokenType.False:
                    writer.WriteBooleanValue(false);
                    break;
                case JsonTokenType.Null:
                    writer.WriteNullValue();
                    break;
                case JsonTokenType.Comment:
                    writer.WriteCommentValue(reader.GetComment());
                    break;
            }
        }

        writer.Flush();
        var formattedJsonBytes = ApplyRawNumberReplacements(stream.ToArray(), rawNumberReplacements);
        var formattedJson = Encoding.UTF8.GetString(formattedJsonBytes);

        return formattedJson;
    }

    private static void WriteRawNumberValue(
        Utf8JsonWriter writer,
        Utf8JsonReader reader,
        MemoryStream stream,
        List<RawNumberReplacement> rawNumberReplacements)
    {
        const long placeholder = 9876543210;
        const string placeholderText = "9876543210";
        var placeholderBytes = Encoding.UTF8.GetBytes(placeholderText);
        var rawNumberBytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();

        // Utf8JsonWriter owns separators, indentation, and comments. Write a finite placeholder so it
        // advances its state correctly, then replace only the placeholder token with the original
        // validated JSON number text. This preserves exact number lexemes that .NET numeric types
        // cannot represent without rounding or normalization.
        writer.Flush();
        var segmentStart = checked((int)stream.Position);
        writer.WriteNumberValue(placeholder);
        writer.Flush();
        var segmentEnd = checked((int)stream.Position);
        var buffer = stream.GetBuffer();
        var placeholderOffset = buffer.AsSpan(segmentStart, segmentEnd - segmentStart).IndexOf(placeholderBytes);
        if (placeholderOffset < 0)
        {
            throw new JsonException("Unable to preserve JSON number text while formatting.");
        }

        rawNumberReplacements.Add(new RawNumberReplacement(
            Start: segmentStart + placeholderOffset,
            Length: placeholderBytes.Length,
            RawNumberBytes: rawNumberBytes));
    }

    private static byte[] ApplyRawNumberReplacements(byte[] formattedJsonBytes, List<RawNumberReplacement> rawNumberReplacements)
    {
        if (rawNumberReplacements.Count == 0)
        {
            return formattedJsonBytes;
        }

        using var output = new MemoryStream();
        var currentPosition = 0;

        foreach (var replacement in rawNumberReplacements)
        {
            output.Write(formattedJsonBytes.AsSpan(currentPosition, replacement.Start - currentPosition));
            output.Write(replacement.RawNumberBytes);
            currentPosition = replacement.Start + replacement.Length;
        }

        output.Write(formattedJsonBytes.AsSpan(currentPosition));
        return output.ToArray();
    }

    public static bool CouldBeJson(string? input)
    {
        if (!TrySkipLeadingWhitespace(input, out var i))
        {
            return false;
        }

        var first = input[i.Value];

        return first switch
        {
            '/' => true,   // Comment
            '{' => true,   // Object
            '[' => true,   // Array
            '"' => true,   // String
            '-' => true,   // Negative number
            >= '0' and <= '9' => true, // Number
            't' => input.AsSpan(i.Value).StartsWith("true"),  // true
            'f' => input.AsSpan(i.Value).StartsWith("false"), // false
            'n' => input.AsSpan(i.Value).StartsWith("null"),  // null
            _ => false
        };
    }

    public static bool CouldBeXml(string? input)
    {
        if (!TrySkipLeadingWhitespace(input, out var i))
        {
            return false;
        }

        // XML must start with '<' after whitespace
        if (input[i.Value] != '<')
        {
            return false;
        }

        // Peek ahead for common XML starts
        var span = input.AsSpan(i.Value);

        return span.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("<!--", StringComparison.Ordinal)
            || span.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || span.Length > 1 && char.IsLetter(span[1]); // element name
    }

    private static bool TrySkipLeadingWhitespace([NotNullWhen(true)] string? input, [NotNullWhen(true)] out int? index)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            index = null;
            return false;
        }

        // Skip leading whitespace
        index = 0;
        while (index < input.Length && char.IsWhiteSpace(input[index.Value]))
        {
            index++;
        }

        if (index == input.Length)
        {
            index = null;
            return false;
        }

        return true;
    }

    internal void UpdateFormat(string newFormat)
    {
        if (newFormat == DashboardUIHelpers.XmlFormat)
        {
            if (TryFormatXml(Text, out var formattedXml))
            {
                ChangeFormattedText(newFormat, formattedXml);
            }
        }
        else if (newFormat == DashboardUIHelpers.JsonFormat)
        {
            if (TryFormatJson(Text, out var formattedJson))
            {
                ChangeFormattedText(newFormat, formattedJson);
            }
        }
        else if (newFormat == DashboardUIHelpers.MarkdownFormat)
        {
            ChangeFormattedText(newFormat, Text);
        }
        else
        {
            ChangeFormattedText(DashboardUIHelpers.PlaintextFormat, Text);
        }
    }

    private sealed record RawNumberReplacement(int Start, int Length, byte[] RawNumberBytes);
}
