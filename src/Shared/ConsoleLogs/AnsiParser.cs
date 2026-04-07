// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Shared.ConsoleLogs;

public class AnsiParser
{
    private const char EscapeChar = '\x1B';
    private const char ParametersSeparatorChar = ';';
    private const char DisplayAttributesFinalByte = 'm';
    private const int ResetCode = 0;
    private const int IncreasedIntensityCode = 1;
    private const int ItalicCode = 3;
    private const int UnderlineCode = 4;
    private const int StrikeThroughCode = 9;
    private const int NormalIntensityCode = 22;
    private const int DefaultForegroundCode = 39;
    private const int DefaultBackgroundCode = 49;
    private const int XtermForegroundSequenceCode = 38;
    private const int XtermBackgroundSequenceCode = 48;

    public static string StripControlSequences(string text)
    {
        StringBuilder? outputBuilder = null;
        var span = text.AsSpan();
        var currentPos = 0;
        var lastWritePos = 0;

        while (currentPos < text.Length)
        {
            var nextEscapeIndex = text.IndexOf(EscapeChar, currentPos);
            if (nextEscapeIndex == -1)
            {
                if (outputBuilder != null)
                {
                    // Write remaining text.
                    outputBuilder.Append(text[lastWritePos..]);
                    break;
                }

                // No escape sequence found, and no text has been escaped. Return the original text.
                return text;
            }

            if (IsConEmuSequence(span[currentPos..], ref currentPos) ||
                IsControlSequence(span[currentPos..], ref currentPos, out _, out _) ||
                IsLinkControlSequence(span[currentPos..], ref currentPos, out _))
            {
                // Append text before the escape sequence, then advance the cursor past the escape sequence
                outputBuilder ??= new StringBuilder(text.Length);
                outputBuilder.Append(text[lastWritePos..nextEscapeIndex]);

                currentPos++;
                lastWritePos = currentPos;
            }
            else
            {
                currentPos++;
            }
        }

        return outputBuilder?.ToString() ?? text;
    }

    public static ConversionResult ConvertToHtml(string? text, ParserState? priorResidualState = null, ConsoleColor? defaultBackgroundColor = null)
    {
        var textStartIndex = -1;
        var textLength = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new(text, default);
        }

        var span = text.AsSpan();

        ParserState currentState = default;
        var newState = priorResidualState ?? default;

        var outputBuilder = new StringBuilder(text.Length * 2);

        for (var i = 0; i < span.Length; i++)
        {
            if (IsConEmuSequence(span[i..], ref i))
            {
                // If we have a control sequence, but have found some text already,
                // we need to write out the new styles (if applicable) and that text
                // before we continue
                if (textStartIndex != -1)
                {
                    if (newState != currentState)
                    {
                        outputBuilder.Append(ProcessStateChange(currentState, newState));
                        currentState = newState;
                    }
                    outputBuilder.Append(text[textStartIndex..(textStartIndex + textLength)]);
                    textStartIndex = -1;
                    textLength = 0;
                }

                continue;
            }

            if (IsLinkControlSequence(span[i..], ref i, out var url))
            {
                // If we have a control sequence, but have found some text already,
                // we need to write out the new styles (if applicable) and that text
                // before we continue
                if (textStartIndex != -1)
                {
                    if (newState != currentState)
                    {
                        outputBuilder.Append(ProcessStateChange(currentState, newState));
                        currentState = newState;
                    }
                    outputBuilder.Append(text[textStartIndex..(textStartIndex + textLength)]);
                    textStartIndex = -1;
                    textLength = 0;
                }

                // Append the URL unformatted, the Url matcher will convert to link later.
                outputBuilder.Append(url);

                continue;
            }

            if (IsControlSequence(span[i..], ref i, out var finalByte, out var parameters))
            {
                // If we have a control sequence, but have found some text already,
                // we need to write out the new styles (if applicable) and that text
                // before we continue
                if (textStartIndex != -1)
                {
                    if (newState != currentState)
                    {
                        outputBuilder.Append(ProcessStateChange(currentState, newState));
                        currentState = newState;
                    }
                    outputBuilder.Append(text[textStartIndex..(textStartIndex + textLength)]);
                    textStartIndex = -1;
                    textLength = 0;
                }

                // The only sequences we care about are display sequences.
                // Ignore everything else and don't write sequence to the output.
                if (finalByte == DisplayAttributesFinalByte)
                {
                    ProcessParameters(defaultBackgroundColor, ref newState, parameters);
                }

                continue;
            }

            // If it wasn't a control sequence, then it must be text, so figure
            // out how much text before the next control sequence (if any)
            if (textStartIndex == -1)
            {
                textStartIndex = i;
            }
            var nextEscapeIndex = -1;
            if (i < text.Length - 1)
            {
                nextEscapeIndex = text.IndexOf(EscapeChar, i + 1);
            }

            // If there's no more control sequences, capture the text length and we're done
            if (nextEscapeIndex < 0)
            {
                textLength = text.Length - textStartIndex;
                break;
            }

            // If there is another control sequence, capture the text length and process it
            textLength = nextEscapeIndex - textStartIndex;
            i = nextEscapeIndex - 1;
        }

        // If we reached the end and have built up a new style, write it out
        if (newState != currentState)
        {
            outputBuilder.Append(ProcessStateChange(currentState, newState));
            currentState = newState;
        }

        // If there's any text left, right that out too
        if (textStartIndex != -1)
        {
            outputBuilder.Append(text[textStartIndex..(textStartIndex + textLength)]);
        }

        // Ensure we always close off the current span. The next log line will
        // pick up the residual state if necessary
        outputBuilder.Append(ProcessStateChange(currentState, default));

        return new(outputBuilder.ToString(), currentState);
    }

    private static void ProcessParameters(ConsoleColor? defaultBackgroundColor, ref ParserState newState, int[] parameters)
    {
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            // If Xterm color sequence
            if (parameter is XtermForegroundSequenceCode or XtermBackgroundSequenceCode && i + 2 < parameters.Length)
            {
                var colorCode = parameters[i + 2];
                if (colorCode is >= 0 and < 256)
                {
                    if (parameter == XtermBackgroundSequenceCode)
                    {
                        newState.XtermBackgroundColorCode = colorCode;
                    }
                    else if (parameter == XtermForegroundSequenceCode)
                    {
                        newState.XtermForegroundColorCode = colorCode;
                    }
                }

                // Skip ahead 2 more parameters that are part of the Xterm color sequence
                i += 2;
            }
            else if (TryGetForegroundColor(parameter, out var color))
            {
                newState.ForegroundColor = color;
            }
            else if (TryGetBackgroundColor(parameter, out color))
            {
                // Don't set the background color if it matches the default background color.
                // Skipping setting it improves appearance when row mouseover slightly changes color.
                newState.BackgroundColor = (color != defaultBackgroundColor) ? color : null;
            }
            else if (parameter == DefaultBackgroundCode)
            {
                newState.BackgroundColor = null;
            }
            else if (parameter == DefaultForegroundCode)
            {
                newState.ForegroundColor = null;
            }
            else if (parameter == NormalIntensityCode)
            {
                newState.Bright = false;
            }
            else if (parameter == ResetCode)
            {
                newState = default;
            }
            else if (parameter == IncreasedIntensityCode)
            {
                newState.Bright = true;
            }
            else if (parameter == UnderlineCode)
            {
                newState.Underline = true;
            }
            else if (parameter == ItalicCode)
            {
                newState.Italic = true;
            }
            else if (parameter == StrikeThroughCode)
            {
                newState.StrikeThrough = true;
            }
        }
    }

    private static bool IsControlSequence(ReadOnlySpan<char> span, ref int position, out char finalByte, out int[] parameters)
    {
        // If we're at \x1B[
        if (span.Length <= 2 || (span[0] != EscapeChar || span[1] != '['))
        {
            parameters = [];
            finalByte = default;
            return false;
        }

        // Find the index of the final byte. Char in range of: @A–Z[\]^_`a–z{|}~
        var paramsEndPosition = span.Slice(2).IndexOfAnyInRange('@', '~');
        if (paramsEndPosition < 0)
        {
            // No end of escape with final byte, cannot parse params.
            parameters = [];
            finalByte = default;
            return false;
        }
        paramsEndPosition += 2;

        // Find the index of the next escape character
        var nextEscapePosition = SubIndexOfSpan(span, EscapeChar, 1);
        if (nextEscapePosition != -1 && nextEscapePosition < paramsEndPosition)
        {
            // Current sequence is not finished before the next escape sequence starts.
            parameters = [];
            finalByte = default;
            return false;
        }

        // Save where current parameter start location
        var currentParamStartPosition = 2;

        // List to store all parameters
        List<int> ret = new(2);

        for (var i = currentParamStartPosition; i <= paramsEndPosition; i++)
        {
            if (span[i] == ParametersSeparatorChar || i == paramsEndPosition)
            {
                // Try to parse the parameter
                if (int.TryParse(
                    span[currentParamStartPosition..i],
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parameterValue))
                {
                    // Add the parameter to the list
                    ret.Add(parameterValue);
                }

                // Move current parameter start to the next character
                currentParamStartPosition = i + 1;
            }
        }

        // Advance the position in the span to the end of the control sequence
        position += paramsEndPosition;
        parameters = [.. ret];
        finalByte = span[paramsEndPosition];

        return true;
    }

    private static bool IsConEmuSequence(ReadOnlySpan<char> span, ref int position)
    {
        // If we're at \x1B]
        if (span.Length <= 2 || (span[0] != EscapeChar || span[1] != ']'))
        {
            return false;
        }

        // Find the index of the end character.
        // End character can be either \x1B (ESC) or \x07 (BELL)
        var endEscPosition = span.IndexOf("\x1B\\");
        var endBellPosition = span.IndexOf("\x07");

        int paramsEndPosition;
        if (endEscPosition != -1 && endBellPosition != -1)
        {
            if (endEscPosition < endBellPosition)
            {
                paramsEndPosition = endEscPosition + 1;
            }
            else
            {
                paramsEndPosition = endBellPosition;
            }
        }
        else if (endEscPosition != -1)
        {
            paramsEndPosition = endEscPosition + 1;
        }
        else if (endBellPosition != -1)
        {
            paramsEndPosition = endBellPosition;
        }
        else
        {
            // No end of escape, cannot parse params.
            return false;
        }

        // Advance the position in the span to the end of the control sequence
        position += paramsEndPosition;

        return true;
    }

    private static bool IsLinkControlSequence(ReadOnlySpan<char> span, ref int position, out string? url)
    {
        url = null;

        // Link sequence
        // \x1B]8;{params};{url}\x1B\\{url-text}\x1B]8;;\x1B\\

        // If we're at \x1B[
        // Links are minimum 5 chars
        if (span.Length <= 5 || (span[0] != EscapeChar || span[1] != ']'))
        {
            return false;
        }

        // Only supported Os sequence is links
        if (span[2] != '8' || span[3] != ';')
        {
            return false;
        }

        // Find the position where the url section ends
        var urlEndEscapePosition = SubIndexOfSpan(span, EscapeChar, 4);
        if (urlEndEscapePosition < 0 || span.Length < urlEndEscapePosition + 1 || span[urlEndEscapePosition + 1] != '\\')
        {
            return false;
        }

        // Find the position where the url-text section ends
        // Continue to search until the following char is ']', could be color/mode formatting escape sequences mixed in
        var linkEndEscapePosition = SubIndexOfSpan(span, EscapeChar, urlEndEscapePosition + 1);
        while (linkEndEscapePosition != -1 && span.Length > (linkEndEscapePosition + 2) && span[linkEndEscapePosition + 1] != ']')
        {
            linkEndEscapePosition = SubIndexOfSpan(span, EscapeChar, linkEndEscapePosition + 1);
        }

        // If we didn't find the end of the url-text sequence return false
        if (linkEndEscapePosition < 0 || span.Length < linkEndEscapePosition + 2 || span[linkEndEscapePosition + 2] != '8')
        {
            return false;
        }

        // Find the position where the whole link sequence ends
        var linkEndPosition = SubIndexOfSpan(span, '\\', linkEndEscapePosition);
        if (linkEndPosition < 0)
        {
            return false;
        }

        var urlSpan = span[4..urlEndEscapePosition];

        // Fin the position where the params section within the url section ends.
        var argsEndPosition = urlSpan.IndexOf(';');
        if (argsEndPosition < 0)
        {
            return false;
        }

        // Return the extracted url
        url = urlSpan[(argsEndPosition + 1)..].ToString();

        // Advance the position in the external span to the end of the whole control sequence
        position += linkEndPosition;

        return true;
    }

    private static string ProcessStateChange(ParserState currentState, ParserState newState)
    {
        var closeSpanIfNeeded = "";
        if (currentState != default)
        {
            closeSpanIfNeeded += "</span>";
        }

        var classes = new List<string>(2);
        var styles = new List<string>(2);

        if (newState.ForegroundColor.HasValue)
        {
            classes.Add(GetForegroundColorClass(newState)!);
        }

        if (newState.BackgroundColor.HasValue)
        {
            classes.Add(GetBackgroundColorClass(newState)!);
        }

        if (newState.Underline)
        {
            classes.Add("ansi-underline");
        }
        if (newState.Italic)
        {
            classes.Add("ansi-italic");
        }
        if (newState.StrikeThrough)
        {
            classes.Add("ansi-strikethrough");
        }

        if (newState.XtermForegroundColorCode.HasValue)
        {
            var colorValue = newState.XtermForegroundColorCode.Value;
            if (TryGetXtermRgbHexColor(colorValue, out var rgbForegroundHex))
            {
                styles.Add($"color: {rgbForegroundHex}");
            }
        }

        if (newState.XtermBackgroundColorCode.HasValue)
        {
            var colorValue = newState.XtermBackgroundColorCode.Value;
            if (TryGetXtermRgbHexColor(colorValue, out var rgbBackgroundHex))
            {
                styles.Add($"background-color: {rgbBackgroundHex}");
            }
        }

        if (classes.Count == 0 && styles.Count == 0)
        {
            return closeSpanIfNeeded;
        }

        var combined = closeSpanIfNeeded + "<span ";

        if (classes.Count > 0)
        {
            combined += $"class=\"{string.Join(" ", classes)}\" ";
        }

        if (styles.Count > 0)
        {
            combined += $"style=\"{string.Join(";", styles)}\" ";
        }

        return combined.TrimEnd() + ">";
    }

    private static string? GetForegroundColorClass(ParserState state)
    {
        return state.ForegroundColor switch
        {
            ConsoleColor.Black => state.Bright ? "ansi-fg-brightblack" : "ansi-fg-black",
            ConsoleColor.Blue => state.Bright ? "ansi-fg-brightblue" : "ansi-fg-blue",
            ConsoleColor.Cyan => state.Bright ? "ansi-fg-brightcyan" : "ansi-fg-cyan",
            ConsoleColor.Green => state.Bright ? "ansi-fg-brightgreen" : "ansi-fg-green",
            ConsoleColor.Magenta => state.Bright ? "ansi-fg-brightmagenta" : "ansi-fg-magenta",
            ConsoleColor.Red => state.Bright ? "ansi-fg-brightred" : "ansi-fg-red",
            ConsoleColor.White => state.Bright ? "ansi-fg-brightwhite" : "ansi-fg-white",
            ConsoleColor.Yellow => state.Bright ? "ansi-fg-brightyellow" : "ansi-fg-yellow",
            _ => ""
        };
    }

    private static string? GetBackgroundColorClass(ParserState state)
    {
        return state.BackgroundColor switch
        {
            ConsoleColor.Black => "ansi-bg-black",
            ConsoleColor.Blue => "ansi-bg-blue",
            ConsoleColor.Cyan => "ansi-bg-cyan",
            ConsoleColor.Green => "ansi-bg-green",
            ConsoleColor.Magenta => "ansi-bg-magenta",
            ConsoleColor.Red => "ansi-bg-red",
            ConsoleColor.White => "ansi-bg-white",
            ConsoleColor.Yellow => "ansi-bg-yellow",
            _ => ""
        };
    }

    private static bool TryGetForegroundColor(int number, out ConsoleColor? color)
    {
        color = number switch
        {
            30 => ConsoleColor.Black,
            31 => ConsoleColor.Red,
            32 => ConsoleColor.Green,
            33 => ConsoleColor.Yellow,
            34 => ConsoleColor.Blue,
            35 => ConsoleColor.Magenta,
            36 => ConsoleColor.Cyan,
            37 => ConsoleColor.White,
            _ => null
        };
        return color != null || number == 39;
    }

    private static bool TryGetBackgroundColor(int number, out ConsoleColor? color)
    {
        color = number switch
        {
            40 => ConsoleColor.Black,
            41 => ConsoleColor.Red,
            42 => ConsoleColor.Green,
            43 => ConsoleColor.Yellow,
            44 => ConsoleColor.Blue,
            45 => ConsoleColor.Magenta,
            46 => ConsoleColor.Cyan,
            47 => ConsoleColor.White,
            _ => null
        };
        return color != null || number == 49;
    }

    private static bool TryGetXtermRgbHexColor(int number, out string? rgbHex)
    {
        rgbHex = number switch
        {
            0 => "#000000",
            1 => "#800000",
            2 => "#008000",
            3 => "#808000",
            4 => "#000080",
            5 => "#800080",
            6 => "#008080",
            7 => "#c0c0c0",
            8 => "#808080",
            9 => "#ff0000",
            10 => "#00ff00",
            11 => "#ffff00",
            12 => "#0000ff",
            13 => "#ff00ff",
            14 => "#00ffff",
            15 => "#ffffff",
            16 => "#000000",
            17 => "#00005f",
            18 => "#000087",
            19 => "#0000af",
            20 => "#0000d7",
            21 => "#0000ff",
            22 => "#005f00",
            23 => "#005f5f",
            24 => "#005f87",
            25 => "#005faf",
            26 => "#005fd7",
            27 => "#005fff",
            28 => "#008700",
            29 => "#00875f",
            30 => "#008787",
            31 => "#0087af",
            32 => "#0087d7",
            33 => "#0087ff",
            34 => "#00af00",
            35 => "#00af5f",
            36 => "#00af87",
            37 => "#00afaf",
            38 => "#00afd7",
            39 => "#00afff",
            40 => "#00d700",
            41 => "#00d75f",
            42 => "#00d787",
            43 => "#00d7af",
            44 => "#00d7d7",
            45 => "#00d7ff",
            46 => "#00ff00",
            47 => "#00ff5f",
            48 => "#00ff87",
            49 => "#00ffaf",
            50 => "#00ffd7",
            51 => "#00ffff",
            52 => "#5f0000",
            53 => "#5f005f",
            54 => "#5f0087",
            55 => "#5f00af",
            56 => "#5f00d7",
            57 => "#5f00ff",
            58 => "#5f5f00",
            59 => "#5f5f5f",
            60 => "#5f5f87",
            61 => "#5f5faf",
            62 => "#5f5fd7",
            63 => "#5f5fff",
            64 => "#5f8700",
            65 => "#5f875f",
            66 => "#5f8787",
            67 => "#5f87af",
            68 => "#5f87d7",
            69 => "#5f87ff",
            70 => "#5faf00",
            71 => "#5faf5f",
            72 => "#5faf87",
            73 => "#5fafaf",
            74 => "#5fafd7",
            75 => "#5fafff",
            76 => "#5fd700",
            77 => "#5fd75f",
            78 => "#5fd787",
            79 => "#5fd7af",
            80 => "#5fd7d7",
            81 => "#5fd7ff",
            82 => "#5fff00",
            83 => "#5fff5f",
            84 => "#5fff87",
            85 => "#5fffaf",
            86 => "#5fffd7",
            87 => "#5fffff",
            88 => "#870000",
            89 => "#87005f",
            90 => "#870087",
            91 => "#8700af",
            92 => "#8700d7",
            93 => "#8700ff",
            94 => "#875f00",
            95 => "#875f5f",
            96 => "#875f87",
            97 => "#875faf",
            98 => "#875fd7",
            99 => "#875fff",
            100 => "#878700",
            101 => "#87875f",
            102 => "#878787",
            103 => "#8787af",
            104 => "#8787d7",
            105 => "#8787ff",
            106 => "#87af00",
            107 => "#87af5f",
            108 => "#87af87",
            109 => "#87afaf",
            110 => "#87afd7",
            111 => "#87afff",
            112 => "#87d700",
            113 => "#87d75f",
            114 => "#87d787",
            115 => "#87d7af",
            116 => "#87d7d7",
            117 => "#87d7ff",
            118 => "#87ff00",
            119 => "#87ff5f",
            120 => "#87ff87",
            121 => "#87ffaf",
            122 => "#87ffd7",
            123 => "#87ffff",
            124 => "#af0000",
            125 => "#af005f",
            126 => "#af0087",
            127 => "#af00af",
            128 => "#af00d7",
            129 => "#af00ff",
            130 => "#af5f00",
            131 => "#af5f5f",
            132 => "#af5f87",
            133 => "#af5faf",
            134 => "#af5fd7",
            135 => "#af5fff",
            136 => "#af8700",
            137 => "#af875f",
            138 => "#af8787",
            139 => "#af87af",
            140 => "#af87d7",
            141 => "#af87ff",
            142 => "#afaf00",
            143 => "#afaf5f",
            144 => "#afaf87",
            145 => "#afafaf",
            146 => "#afafd7",
            147 => "#afafff",
            148 => "#afd700",
            149 => "#afd75f",
            150 => "#afd787",
            151 => "#afd7af",
            152 => "#afd7d7",
            153 => "#afd7ff",
            154 => "#afff00",
            155 => "#afff5f",
            156 => "#afff87",
            157 => "#afffaf",
            158 => "#afffd7",
            159 => "#afffff",
            160 => "#d70000",
            161 => "#d7005f",
            162 => "#d70087",
            163 => "#d700af",
            164 => "#d700d7",
            165 => "#d700ff",
            166 => "#d75f00",
            167 => "#d75f5f",
            168 => "#d75f87",
            169 => "#d75faf",
            170 => "#d75fd7",
            171 => "#d75fff",
            172 => "#d78700",
            173 => "#d7875f",
            174 => "#d78787",
            175 => "#d787af",
            176 => "#d787d7",
            177 => "#d787ff",
            178 => "#d7af00",
            179 => "#d7af5f",
            180 => "#d7af87",
            181 => "#d7afaf",
            182 => "#d7afd7",
            183 => "#d7afff",
            184 => "#d7d700",
            185 => "#d7d75f",
            186 => "#d7d787",
            187 => "#d7d7af",
            188 => "#d7d7d7",
            189 => "#d7d7ff",
            190 => "#d7ff00",
            191 => "#d7ff5f",
            192 => "#d7ff87",
            193 => "#d7ffaf",
            194 => "#d7ffd7",
            195 => "#d7ffff",
            196 => "#ff0000",
            197 => "#ff005f",
            198 => "#ff0087",
            199 => "#ff00af",
            200 => "#ff00d7",
            201 => "#ff00ff",
            202 => "#ff5f00",
            203 => "#ff5f5f",
            204 => "#ff5f87",
            205 => "#ff5faf",
            206 => "#ff5fd7",
            207 => "#ff5fff",
            208 => "#ff8700",
            209 => "#ff875f",
            210 => "#ff8787",
            211 => "#ff87af",
            212 => "#ff87d7",
            213 => "#ff87ff",
            214 => "#ffaf00",
            215 => "#ffaf5f",
            216 => "#ffaf87",
            217 => "#ffafaf",
            218 => "#ffafd7",
            219 => "#ffafff",
            220 => "#ffd700",
            221 => "#ffd75f",
            222 => "#ffd787",
            223 => "#ffd7af",
            224 => "#ffd7d7",
            225 => "#ffd7ff",
            226 => "#ffff00",
            227 => "#ffff5f",
            228 => "#ffff87",
            229 => "#ffffaf",
            230 => "#ffffd7",
            231 => "#ffffff",
            232 => "#080808",
            233 => "#121212",
            234 => "#1c1c1c",
            235 => "#262626",
            236 => "#303030",
            237 => "#3a3a3a",
            238 => "#444444",
            239 => "#4e4e4e",
            240 => "#585858",
            241 => "#626262",
            242 => "#6c6c6c",
            243 => "#767676",
            244 => "#808080",
            245 => "#8a8a8a",
            246 => "#949494",
            247 => "#9e9e9e",
            248 => "#a8a8a8",
            249 => "#b2b2b2",
            250 => "#bcbcbc",
            251 => "#c6c6c6",
            252 => "#d0d0d0",
            253 => "#dadada",
            254 => "#e4e4e4",
            255 => "#eeeeee",
            _ => null
        };

        return rgbHex != null;
    }

    private static int SubIndexOfSpan(ReadOnlySpan<char> span, char value, int startIndex = 0)
    {
        var indexInSlice = span[startIndex..].IndexOf(value);

        if (indexInSlice < 0)
        {
            return indexInSlice;
        }

        return startIndex + indexInSlice;
    }

    public record struct ParserState
    {
        public ConsoleColor? ForegroundColor { get; set; }
        public ConsoleColor? BackgroundColor { get; set; }
        public int? XtermForegroundColorCode { get; set; }
        public int? XtermBackgroundColorCode { get; set; }
        public bool Bright { get; set; }
        public bool Underline { get; set; }
        public bool Italic { get; set; }
        public bool StrikeThrough { get; set; }
    }

    public readonly record struct ConversionResult(string? ConvertedText, ParserState ResidualState);
}
