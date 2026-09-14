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
