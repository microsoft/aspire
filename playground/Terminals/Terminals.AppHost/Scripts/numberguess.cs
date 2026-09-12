// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A deliberately old-fashioned interactive console game, run as a .NET file-based app
// (`dotnet run --file numberguess.cs -- <limit>`).
//
// It exists to demonstrate driving an interactive process from AppHost code: the AppHost shows this program in an
// InputType.Terminal interaction, then plays it by typing guesses and reading the replies back off the terminal
// screen. Nothing here knows it is being automated - it is an ordinary Console.ReadLine app.
//
// The reply format is the contract the automation relies on, so it is deliberately unambiguous:
//
//     Guess #1: 50            <- the prompt, plus the tty's echo of what was typed
//       >> #1: 50 is too high <- the reply, tagged with the attempt number
//
// Tagging each reply with its attempt number means the automation can wait for ">> #3:" and be certain it is
// reading the response to its third guess rather than a stale line still on screen from an earlier one.

var limit = args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 1 ? parsed : 100;
var secret = Random.Shared.Next(1, limit + 1);

Console.WriteLine();
Console.WriteLine("+------------------------------------------+");
Console.WriteLine("|          N U M B E R   G U E S S         |");
Console.WriteLine("+------------------------------------------+");
Console.WriteLine();
Console.WriteLine($"I'm thinking of a number between 1 and {limit}.");
Console.WriteLine("Type a guess and press Enter. I'll tell you if it's too high or too low.");
Console.WriteLine();

for (var attempt = 1; ; attempt++)
{
    Console.Write($"Guess #{attempt}: ");

    var line = Console.ReadLine();
    if (line is null)
    {
        // stdin closed - the terminal went away.
        return 1;
    }

    if (!int.TryParse(line.Trim(), out var guess))
    {
        Console.WriteLine($"  >> #{attempt}: '{line.Trim()}' is not a number");
        continue;
    }

    if (guess < secret)
    {
        Console.WriteLine($"  >> #{attempt}: {guess} is too low");
    }
    else if (guess > secret)
    {
        Console.WriteLine($"  >> #{attempt}: {guess} is too high");
    }
    else
    {
        Console.WriteLine($"  >> #{attempt}: {guess} is correct");
        Console.WriteLine();
        Console.WriteLine($"Got it in {attempt} {(attempt == 1 ? "guess" : "guesses")}. The number was {secret}.");

        // Block rather than exit so the final screen stays live until whoever owns the terminal tears it down.
        // Exiting immediately would race the automation's "let the human read the result" pause.
        Console.ReadLine();
        return 0;
    }
}
