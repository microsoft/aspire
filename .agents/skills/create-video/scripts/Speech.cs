using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// The voice: narration of a storyboard, voice samples, and pronunciation samples. The speech comes from the
/// Microsoft Edge voice service, through the msedge-tts npm package (tts/tts.mjs).
/// </summary>
static class Speech
{
    // The voice of every Aspire video, unless the storyboard picks another ("voice", "rate"). See references/specs.md.
    public const string Voice = "en-US-AndrewMultilingualNeural";
    public const string Rate = "+2%";

    // How long a step lasts in a video without narration, unless it says ("seconds").
    public const double SilentStepSeconds = 3.0;

    // How the voice must say words it misreads. The captions keep the written form. A storyboard can add its own in
    // "spoken"; add the ones that apply to every video here.
    private static readonly Dictionary<string, string> s_spoken = new()
    {
        ["ASP.NET"] = "A.S.P. .NET",
    };

    // The shortlist to offer, in English: (voice, description). The first one is the default voice.
    public static readonly (string Voice, string Description)[] Shortlist =
    [
        (Voice, "Male, warm and confident (the default)"),
        ("en-US-BrianMultilingualNeural", "Male, approachable and casual"),
        ("en-US-ChristopherNeural", "Male, reliable, with authority"),
        ("en-US-AvaMultilingualNeural", "Female, expressive and friendly"),
        ("en-US-EmmaMultilingualNeural", "Female, cheerful and clear"),
        ("en-US-JennyNeural", "Female, friendly and considerate"),
    ];

    private const string SampleLine = "Aspire runs your whole app with one command, and the dashboard shows every resource, its logs, and its traces.";

    private static string Tts => Path.Combine(Brand.SkillTools, "tts", "tts.mjs");

    private static string ApplySpoken(string text, JsonObject? overrides)
    {
        // The storyboard's entries replace the built-in ones for the same word, such as a Spanish "ASP.NET": merged
        // first, then applied once, so a built-in replacement can't hide the word from the storyboard's.
        var spoken = new Dictionary<string, string>(s_spoken);
        foreach (var (written, said) in overrides ?? [])
        {
            spoken[written] = (string)said!;
        }

        foreach (var (written, said) in spoken)
        {
            text = text.Replace(written, said, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Writes text, spoken with the voice, to an mp3. words: returns the words the voice speaks.</summary>
    public static string[] Speak(string text, string path, string voice, string rate, bool words = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (exitCode, output, error) = Media.Run(Media.Node, [Tts, voice, rate, path, .. words ? ["--words"] : Array.Empty<string>()], text);
            if (exitCode == 0)
            {
                return words ? [.. JsonNode.Parse(output)!.AsArray().Select(word => (string)word!)] : [];
            }

            if (attempt == 4)
            {
                throw new VideoException($"The voice failed: {error.Trim()[^Math.Min(600, error.Trim().Length)..]}");
            }

            Thread.Sleep(2000);
        }
    }

    /// <summary>The voices the Edge voice service offers, whose name starts with prefix.</summary>
    public static List<(string Name, string Gender, string Personalities)> Available(string prefix = "")
    {
        var (exitCode, output, error) = Media.Run(Media.Node, [Tts, "--voices"]);
        if (exitCode != 0)
        {
            throw new VideoException($"Can't list the voices: {error.Trim()}");
        }

        return JsonNode.Parse(output)!.AsArray()
            .Select(voice => ((string)voice!["name"]!, (string)voice["gender"]!, string.Join(", ", voice["personalities"]!.AsArray().Select(p => (string)p!))))
            .Where(voice => voice.Item1.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// narrate &lt;workspace&gt; [--force]: one mp3 per step of storyboard.json, and durations.json, which the
    /// recorder and the assembly read. Only the steps whose text, voice or rate changed are spoken again. A storyboard
    /// with "narration": false has no voice: it only writes durations.json, from each step's "seconds".
    /// </summary>
    public static void Narrate(string workspace, bool force)
    {
        var storyboard = Video.Storyboard(workspace);
        var steps = storyboard["chapters"]!.AsArray()
            .SelectMany(chapter => chapter!["clips"]!.AsArray())
            .SelectMany(clip => clip!["steps"]?.AsArray() ?? [])
            .Select(step => step!.AsObject())
            .ToList();
        var durations = new JsonObject();

        if (!((bool?)storyboard["narration"] ?? true))
        {
            foreach (var step in steps)
            {
                durations[(string)step["id"]!] = (double?)step["seconds"] ?? SilentStepSeconds;
            }

            File.WriteAllText(Path.Combine(workspace, "durations.json"), durations.ToJsonString(new() { WriteIndented = true }));
            Console.WriteLine($"{steps.Count} steps, no narration, {durations.Sum(d => (double)d.Value!) / 60:0.0} minutes");
            return;
        }

        Media.CheckNode();
        Media.CheckFfmpeg();
        var overrides = storyboard["spoken"]?.AsObject();
        var voice = (string?)storyboard["voice"] ?? Voice;
        var rate = (string?)storyboard["rate"] ?? Rate;
        var audio = Directory.CreateDirectory(Path.Combine(workspace, "audio")).FullName;

        foreach (var step in steps)
        {
            var id = (string)step["id"]!;
            var text = ApplySpoken((string?)step["text"] ?? throw new VideoException($"Step {id} has no \"text\"."), overrides);
            // The stamp records what was spoken, with which voice and rate, so a change speaks the step again.
            var said = $"{voice} {rate}\n{text}";
            var path = Path.Combine(audio, $"{id}.mp3");
            var stamp = Path.Combine(audio, $"{id}.txt");
            if (force || !File.Exists(path) || !File.Exists(stamp) || File.ReadAllText(stamp) != said)
            {
                Console.WriteLine($"narrating {id}");
                Speak(text, path, voice, rate);
                File.WriteAllText(stamp, said);
            }

            durations[id] = Math.Round(Media.Duration(path), 2);
        }

        File.WriteAllText(Path.Combine(workspace, "durations.json"), durations.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"{steps.Count} steps, {durations.Sum(d => (double)d.Value!) / 60:0.0} minutes of narration");
    }

    /// <summary>
    /// voices --list [prefix]: the voices the Edge voice service offers; only these work.
    /// voices &lt;output.mp4&gt; ["line"] [voice...]: the same line in each voice (the shortlist by default), on cards
    /// that name them, for a person to pick the narrator.
    /// </summary>
    public static void Voices(string[] args)
    {
        Media.CheckNode();
        if (args[0] == "--list")
        {
            foreach (var (name, gender, personalities) in Available(args.Length > 1 ? args[1] : ""))
            {
                Console.WriteLine($"{name,-36} {gender,-7} {personalities}");
            }

            return;
        }

        Media.CheckFfmpeg();
        var output = Path.GetFullPath(args[0]);
        var rest = args[1..].ToList();
        var line = rest.Count > 0 && !rest[0].Contains("Neural", StringComparison.Ordinal) ? rest[0] : SampleLine;
        if (rest.Count > 0 && line == rest[0])
        {
            rest.RemoveAt(0);
        }

        var voices = rest.Count > 0 ? rest : Shortlist.Select(item => item.Voice).ToList();
        var offered = Available().Select(voice => voice.Name).ToHashSet();
        var missing = voices.Where(voice => !offered.Contains(voice)).ToList();
        if (missing.Count > 0)
        {
            throw new VideoException($"The Edge voice service doesn't offer {string.Join(", ", missing)}: pick from \"voices --list\".");
        }

        Samples(output, voices.Select((voice, index) =>
        {
            var description = Shortlist.FirstOrDefault(item => item.Voice == voice).Description ?? voice;
            Console.WriteLine($"Voice {index + 1}: {voice}");
            return (Brand.Card(DisplayName(voice), description, $"Voice {index + 1}"), line, voice, Rate);
        }));
    }

    /// <summary>
    /// pronounce [--storyboard s] [--voice v] [--rate r] &lt;output.mp4&gt; "line with {}" "written" "spelling"...:
    /// one labeled option per spelling of a word, each saying the same line with the video's voice and rate.
    /// </summary>
    public static void Pronounce(string[] args)
    {
        Media.CheckNode();
        Media.CheckFfmpeg();
        string voice = Voice, rate = Rate;
        var rest = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--storyboard":
                    var storyboard = JsonNode.Parse(File.ReadAllText(args[++index]))!;
                    voice = (string?)storyboard["voice"] ?? voice;
                    rate = (string?)storyboard["rate"] ?? rate;
                    break;
                case "--voice":
                    voice = args[++index];
                    break;
                case "--rate":
                    rate = args[++index];
                    break;
                default:
                    rest.Add(args[index]);
                    break;
            }
        }

        var (output, line, written) = (Path.GetFullPath(rest[0]), rest[1], rest[2]);
        var caption = line.Replace("{}", written, StringComparison.Ordinal);
        Console.WriteLine($"Voice: {voice} at {rate}");
        Samples(output, rest[3..].Select((spelling, index) =>
            (Brand.Card($"Option {index + 1}", caption, "Pronunciation check"), line.Replace("{}", spelling, StringComparison.Ordinal), voice, rate)),
            printWords: true);
    }

    /// <summary>Joins short clips, each a card with a line spoken over it, into one mp4.</summary>
    private static void Samples(string output, IEnumerable<(SkiaSharp.SKBitmap Card, string Line, string Voice, string Rate)> samples, bool printWords = false)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-video-sample-").FullName;
        try
        {
            var parts = new List<string>();
            foreach (var (card, line, voice, rate) in samples)
            {
                var index = parts.Count + 1;
                var mp3 = Path.Combine(directory, $"{index}.mp3");
                var words = Speak(line, mp3, voice, rate, words: printWords);
                if (printWords)
                {
                    Console.WriteLine($"Option {index}: spoken as [{string.Join(", ", words.Select(word => $"\"{word}\""))}]");
                }

                var png = Path.Combine(directory, $"{index}.png");
                Brand.Save(card, png);
                card.Dispose();
                var mp4 = Path.Combine(directory, $"{index}.mp4");
                Media.FfmpegRun(["-loop", "1", "-framerate", Brand.Fps.ToString(CultureInfo.InvariantCulture), "-i", png, "-i", mp3,
                    "-filter_complex", "[1:a]adelay=500|500,apad=pad_dur=0.8[a]", "-map", "0:v", "-map", "[a]", "-shortest",
                    "-c:v", "libx264", "-tune", "stillimage", "-crf", "28", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "1", mp4]);
                parts.Add(mp4);
            }

            var list = Path.Combine(directory, "list.txt");
            File.WriteAllLines(list, parts.Select(Media.ConcatEntry));
            Media.FfmpegRun(["-f", "concat", "-safe", "0", "-i", list, "-c", "copy", output]);
            Console.WriteLine(output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>en-US-AvaMultilingualNeural -> Ava (multilingual, en-US).</summary>
    private static string DisplayName(string voice)
    {
        var parts = voice.Split('-');
        var language = string.Join('-', parts.Take(2));
        var name = parts[^1].Replace("Neural", "", StringComparison.Ordinal);
        var multilingual = name.EndsWith("Multilingual", StringComparison.Ordinal);
        return $"{name.Replace("Multilingual", "", StringComparison.Ordinal)} ({(multilingual ? "multilingual, " : "")}{language})";
    }
}
