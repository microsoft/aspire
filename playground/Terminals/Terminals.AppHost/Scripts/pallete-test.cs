// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Open palette-test in the Terminals playground, then switch Dashboard themes to compare palettes.
// Run directly: dotnet run --file pallete-test.cs (add -- --once to emit the chart and exit).
// Original chart inspired by the block/foreground views in https://github.com/eikenb/terminal-colors.
// SGR reference: https://invisible-island.net/xterm/ctlseqs/ctlseqs.html

using System.Text;

const string Reset = "\u001b[0m";
string[] names = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];
var chart = new StringBuilder();

// Keep default and indexed colors as SGR references (e.g. ESC[31;44m), not resolved RGB.
// This lets the browser recolor existing content and selection without rerunning the script.
string Foreground(int index) => index < 8 ? $"{30 + index}" : $"{90 + index - 8}";
string Background(int index) => index < 8 ? $"{40 + index}" : $"{100 + index - 8}";
string Label(int index) => $"{index:00} {(index < 8 ? "" : "Bright ")}{names[index % 8]}";

void Cell(string text, string foreground, string background, int width)
{
    chart.Append("\u001b[").Append(foreground).Append(';').Append(background).Append('m');
    chart.Append(text.PadRight(width));
    chart.Append(Reset);
}

chart.AppendLine("ASPIRE TERMINAL PALETTE REVIEW");
chart.AppendLine("Switch Dashboard Light / Dark in Settings. Select text to compare selection colors.");
chart.AppendLine("ANSI names identify slots, not fixed RGB values. Low-contrast matrix cells are intentional.");
chart.AppendLine();
chart.AppendLine("DEFAULT SURFACE (uncolored cells use the palette background)");
chart.AppendLine("+--------------------------------------------------------------------------------------------------+");
chart.AppendLine("|                                                                                                  |");
chart.AppendLine("| Default foreground: The quick brown fox jumps over the lazy dog. 0123456789 !? [] {}               |");
chart.AppendLine("+--------------------------------------------------------------------------------------------------+");
chart.AppendLine();
chart.AppendLine("ANSI SWATCHES: blank fill, default text, black text (00), bright-white text (15)");

for (var start = 0; start < 16; start += 8)
{
    chart.AppendLine(start == 0 ? "Normal 00-07" : "Bright 08-15 (explicit bright slots, not bold)");
    for (var index = start; index < start + 8; index++)
    {
        chart.Append($"{index:00} {names[index % 8]}".PadRight(12));
    }
    chart.AppendLine();

    foreach (var (text, foreground) in new[] { ("", "39"), (" Default", "39"), (" Black", "30"), (" White", "97") })
    {
        for (var index = start; index < start + 8; index++)
        {
            Cell(text, foreground, Background(index), 11);
            chart.Append(' ');
        }
        chart.AppendLine();
    }
    chart.AppendLine();
}

chart.AppendLine("TEXT MATRIX: rows = foreground; columns = background; D = palette default");
chart.Append("Foreground".PadRight(18));
foreach (var column in new[] { "D", "00", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12", "13", "14", "15" })
{
    chart.Append(column.PadRight(5));
}
chart.AppendLine();

for (var foreground = -1; foreground < 16; foreground++)
{
    chart.Append((foreground < 0 ? "D Default" : Label(foreground)).PadRight(18));
    for (var background = -1; background < 16; background++)
    {
        Cell(" Aa0 ", foreground < 0 ? "39" : Foreground(foreground), background < 0 ? "49" : Background(background), 5);
    }
    chart.AppendLine();
}

chart.AppendLine();
chart.AppendLine($"ATTRIBUTES: Normal Aa0  \u001b[1mBold Aa0{Reset}  \u001b[2mDim Aa0{Reset}  \u001b[4mUnderline Aa0{Reset}  \u001b[7mReverse Aa0{Reset}");
chart.AppendLine($"ANSI TEXT: \u001b[31mRed{Reset}  \u001b[32mGreen{Reset}  \u001b[33mYellow{Reset}  \u001b[34mBlue{Reset}  \u001b[35mMagenta{Reset}  \u001b[36mCyan{Reset}");
chart.AppendLine($"TRUE RGB CONTROL (does not follow palette): \u001b[38;2;255;128;64mOrange #FF8040{Reset}");
chart.Append("Chart stays still for selection and screenshots. Resize or scroll as needed. Ctrl+C exits.");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

try
{
    Console.Write("\u001b]2;Aspire palette review\u0007");
    Console.Write(Reset);
    Console.Write(chart);
    if (!args.Contains("--once"))
    {
        await Task.Delay(Timeout.Infinite, stopping.Token);
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    // Leave the chart available until the user stops the resource or presses Ctrl+C.
}
finally
{
    Console.Write($"{Reset}\u001b]2;\u0007");
}
