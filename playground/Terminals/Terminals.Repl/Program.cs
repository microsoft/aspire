// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

const string Reset = "\u001b[0m";
const string Bold = "\u001b[1m";
const string Cyan = "\u001b[36m";
const string Green = "\u001b[32m";
const string Yellow = "\u001b[33m";
const string Magenta = "\u001b[35m";

var resourceName = Environment.GetEnvironmentVariable("ASPIRE_RESOURCE_NAME") ?? "repl";
var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

PrintBanner(resourceName, processId);

while (true)
{
    Console.Write($"{Bold}{Magenta}{resourceName}#{processId}{Reset}{Cyan}>{Reset} ");
    var line = Console.ReadLine();
    if (line is null)
    {
        // PTY closed.
        break;
    }

    var trimmed = line.Trim();
    if (trimmed.Length == 0)
    {
        continue;
    }

    var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var command = parts[0].ToLowerInvariant();
    var rest = parts.Length > 1 ? parts[1] : string.Empty;

    switch (command)
    {
        case "help" or "?":
            PrintHelp();
            break;
        case "exit" or "quit":
            Console.WriteLine($"{Yellow}Goodbye from {resourceName} pid {processId}.{Reset}");
            return 0;
        case "clear" or "cls":
            // ANSI clear screen + cursor to home.
            Console.Write("\u001b[2J\u001b[H");
            break;
        case "time":
            Console.WriteLine($"{Green}{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}{Reset}");
            break;
        case "size":
            Console.WriteLine($"{Green}{Console.WindowWidth} cols x {Console.WindowHeight} rows{Reset}");
            break;
        case "rainbow":
            PrintRainbow(rest.Length > 0 ? rest : "Hello from Aspire!");
            break;
        case "kgp":
            PrintKittyImage();
            break;
        case "echo":
            Console.WriteLine(rest);
            break;
        case "whoami":
            Console.WriteLine($"{Bold}{Cyan}{resourceName}{Reset} pid {Bold}{processId}{Reset}");
            break;
        default:
            Console.WriteLine($"{Yellow}Unknown command:{Reset} {trimmed}. Type {Bold}help{Reset} for a list.");
            break;
    }
}

return 0;

static void PrintBanner(string resourceName, string processId)
{
    Console.WriteLine();
    Console.WriteLine($"{Cyan}┌─────────────────────────────────────────────────────┐{Reset}");
    Console.WriteLine($"{Cyan}│{Reset} {Bold}Aspire WithTerminal demo REPL{Reset}                      {Cyan}│{Reset}");
    Console.WriteLine($"{Cyan}│{Reset} resource: {Bold}{Magenta}{resourceName,-20}{Reset} pid: {Bold}{processId,-7}{Reset}    {Cyan}│{Reset}");
    Console.WriteLine($"{Cyan}└─────────────────────────────────────────────────────┘{Reset}");
    Console.WriteLine($"Type {Bold}help{Reset} to see available commands. Type {Bold}exit{Reset} to leave.");
    Console.WriteLine();
}

static void PrintHelp()
{
    Console.WriteLine($"{Bold}Available commands:{Reset}");
    Console.WriteLine($"  {Cyan}help{Reset}                  Show this help");
    Console.WriteLine($"  {Cyan}whoami{Reset}                Show resource name + process id");
    Console.WriteLine($"  {Cyan}time{Reset}                  Show local time");
    Console.WriteLine($"  {Cyan}size{Reset}                  Show terminal dimensions (resize the window!)");
    Console.WriteLine($"  {Cyan}echo <text>{Reset}           Echo a line back");
    Console.WriteLine($"  {Cyan}rainbow [text]{Reset}        Print rainbow text");
    Console.WriteLine($"  {Cyan}kgp{Reset}                   Draw a Kitty graphics image (re-run to replace it)");
    Console.WriteLine($"  {Cyan}clear{Reset}                 Clear the screen");
    Console.WriteLine($"  {Cyan}exit{Reset}                  Quit the REPL");
}

static void PrintRainbow(string text)
{
    string[] colors =
    [
        "\u001b[31m", "\u001b[33m", "\u001b[32m", "\u001b[36m", "\u001b[34m", "\u001b[35m",
    ];

    var sb = new System.Text.StringBuilder();
    for (var i = 0; i < text.Length; i++)
    {
        sb.Append(colors[i % colors.Length]).Append(text[i]);
    }

    sb.Append(Reset);
    Console.WriteLine(sb.ToString());
}

// Emits a Kitty Graphics Protocol image so the full graphics path can be
// exercised from the dashboard: this process writes APC bytes to its PTY,
// Aspire.TerminalHost forwards them verbatim over HMP1, and the dashboard's
// xterm.js image addon decodes and renders them.
//
// Two escape sequences are involved, both of the form
// ESC '_G' <comma-separated key=value pairs> ';' <base64 payload> ESC '\':
//
//   ESC _ G a=t,f=32,s=64,v=64,i=1,t=d,q=2,m=1 ; <base64 chunk> ESC \
//   ESC _ G a=p,i=1,p=1,c=16,r=8,q=2           ;                 ESC \
//
//   a=t   transmit only (a=p places what was transmitted)
//   f=32  payload is 32-bit RGBA, so s/v carry the pixel dimensions
//   t=d   payload travels inline ("direct"); the file/shared-memory
//         transports name backend resources a browser cannot reach
//   i/p   image and placement identity, so re-running this replaces the
//         previous placement instead of stacking a new copy
//   q=2   suppress the terminal's OK/error replies, which would otherwise
//         arrive on this process's stdin and be read as a REPL command
//   m=1   more chunks follow (the final chunk sends m=0)
//
// Spec: https://sw.kovidgoyal.net/kitty/graphics-protocol/
static void PrintKittyImage()
{
    const int Size = 64;
    const int ImageId = 1;
    const int PlacementId = 1;

    var pixels = new byte[Size * Size * 4];
    for (var y = 0; y < Size; y++)
    {
        for (var x = 0; x < Size; x++)
        {
            var offset = ((y * Size) + x) * 4;
            var onBorder = x < 3 || y < 3 || x >= Size - 3 || y >= Size - 3;
            pixels[offset] = onBorder ? (byte)0xFF : (byte)(x * 255 / (Size - 1));
            pixels[offset + 1] = onBorder ? (byte)0xFF : (byte)(y * 255 / (Size - 1));
            pixels[offset + 2] = onBorder ? (byte)0xFF : (byte)0x80;
            pixels[offset + 3] = 0xFF;
        }
    }

    // The protocol caps a single escape sequence's payload at 4096 base64
    // bytes, so anything larger has to be split across chunked sequences.
    const int MaxChunk = 4096;
    var payload = Convert.ToBase64String(pixels);

    for (var sent = 0; sent < payload.Length; sent += MaxChunk)
    {
        var chunk = payload.AsSpan(sent, Math.Min(MaxChunk, payload.Length - sent));
        var more = sent + MaxChunk < payload.Length ? 1 : 0;
        var keys = sent == 0
            ? string.Create(CultureInfo.InvariantCulture, $"a=t,f=32,s={Size},v={Size},i={ImageId},t=d,q=2,m={more}")
            : string.Create(CultureInfo.InvariantCulture, $"m={more}");

        Console.Write($"\u001b_G{keys};{chunk}\u001b\\");
    }

    Console.Write(string.Create(CultureInfo.InvariantCulture, $"\u001b_Ga=p,i={ImageId},p={PlacementId},c=16,r=8,q=2\u001b\\"));
    Console.Out.Flush();
    Console.WriteLine();
    Console.WriteLine($"{Green}Sent a {Size}x{Size} RGBA Kitty image (id={ImageId}, placement={PlacementId}).{Reset}");
}

