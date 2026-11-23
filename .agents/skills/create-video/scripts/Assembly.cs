using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

/// <summary>
/// Assembles a video: title cards, slides, framed recordings, the narration, and captions. Reads storyboard.json,
/// durations.json, and clips/&lt;clip&gt;/timeline.json and frames (from the recorder). Writes one file per rendition
/// (&lt;output&gt;.mp4 for "web", &lt;output&gt;-&lt;name&gt;.mp4 for the others), &lt;output&gt;.vtt and
/// &lt;output&gt;.srt in the workspace. The intermediate files are in build/.
/// </summary>
sealed partial class Assembly
{
    /// <summary>
    /// The renditions a video can be published in, by use (see references/specs.md). The storyboard lists the ones
    /// to make in "renditions" (["web"] by default). Every rendition is encoded from build/master.mp4: the layout is
    /// drawn for 16:9 Full HD, so there's no vertical or 4K rendition. CRF_&lt;NAME&gt; (or CRF, for "web")
    /// overrides a rendition's CRF.
    /// </summary>
    public static readonly Dictionary<string, (int Width, int Height, int Crf, string Bitrate, int Channels, long? Limit)> Renditions = new()
    {
        ["web"] = (1920, 1080, 33, "64k", 1, 10_000_000),     // GitHub pull requests and issues, aspire.dev pages
        ["hd"] = (1920, 1080, 20, "160k", 2, null),           // YouTube, conferences, editing: the best quality
        ["light"] = (1280, 720, 30, "64k", 1, 5_000_000),     // Teams, Slack, email, phones: small and quick to load
    };

    // Timing, in seconds.
    private const double ChapterCardSeconds = 2.6;
    private const double CardLead = 1.0, CardTail = 1.4;
    private const double SlideLead = 0.6, SlideGap = 0.5, SlideTail = 1.0;
    private const double ClipLead = 0.7, ClipTail = 1.0;

    // Zooms in recordings (see references/specs.md): slight, eased, and never so large the element loses its context.
    private const double ZoomSeconds = 0.8;   // the length of a zoom in, out, or from one element to another
    private const double ZoomMax = 1.4;       // the largest zoom, whatever the recorder asks
    private const double ZoomFill = 0.8;      // the zoomed element fills at most this share of the width or the height
    private const double ZoomMin = 1.06;      // a smaller zoom isn't worth the movement: the element stays wide
    private const double ZoomMerge = 1.5;     // a zoom out followed this soon by a zoom in pans to the next element instead

    private static readonly string s_fps = Brand.Fps.ToString(CultureInfo.InvariantCulture);
    private static readonly string[] s_masterVideo = ["-c:v", "libx264", "-preset", "medium", "-crf", "16", "-r", s_fps, "-pix_fmt", "yuv420p"];
    private static readonly string[] s_masterAudio = ["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"];

    private readonly string _workspace;
    private readonly string _build;
    private readonly JsonObject _storyboard;
    private JsonObject? _durations;
    private readonly bool _narrated;
    private readonly bool _branded;

    public Assembly(string workspace)
    {
        _workspace = workspace;
        _build = Directory.CreateDirectory(Path.Combine(workspace, "build")).FullName;
        _storyboard = Video.Storyboard(workspace);
        // "narration": false makes a silent video, without captions; "branding": false, one without the Aspire look.
        _narrated = (bool?)_storyboard["narration"] ?? true;
        _branded = (bool?)_storyboard["branding"] ?? true;
    }

    private double Duration(JsonNode step)
    {
        if (_durations is null)
        {
            var path = Path.Combine(_workspace, "durations.json");
            _durations = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : throw new VideoException("No durations.json: run \"narrate\" first.");
        }

        return (double?)_durations[(string)step["id"]!] ?? throw new VideoException($"No duration for step {(string)step["id"]!}: run \"narrate\" again.");
    }

    private string Audio(JsonNode step) => Path.Combine(_workspace, "audio", $"{(string)step["id"]!}.mp3");

    /// <summary>The narration of a step at an offset; nothing in a video without narration.</summary>
    private List<(string Mp3, double Offset)> Sounds(JsonNode step, double offset) => _narrated ? [(Audio(step), offset)] : [];

    private SKBitmap Card(string title, string subtitle, string? label = null, bool big = false)
        => _branded ? Brand.Card(title, subtitle, label, big) : Brand.PlainCard(title, subtitle, label);

    /// <summary>The inputs and filters that mix narration, a list of (mp3, offset), into [aout]; or silence.</summary>
    private static (List<string> Args, List<string> Filters) Mix(List<(string Mp3, double Offset)> sounds, int firstInput, double seconds)
    {
        if (sounds.Count == 0)
        {
            return (["-f", "lavfi", "-t", Media.Seconds(seconds), "-i", "anullsrc=r=48000:cl=stereo"], [$"[{firstInput}:a]anull[aout]"]);
        }

        List<string> args = [], filters = [];
        for (var index = 0; index < sounds.Count; index++)
        {
            var delay = (int)(sounds[index].Offset * 1000);
            args.AddRange(["-i", sounds[index].Mp3]);
            filters.Add($"[{firstInput + index}:a]adelay={delay}|{delay}[a{index}]");
        }

        var inputs = string.Concat(Enumerable.Range(0, sounds.Count).Select(index => $"[a{index}]"));
        filters.Add($"{inputs}amix=inputs={sounds.Count}:normalize=0,apad,atrim=0:{Media.Seconds(seconds)}[aout]");
        return (args, filters);
    }

    /// <summary>A still image, with narration: a list of (mp3, offset in seconds).</summary>
    private void Still(SKBitmap image, string name, double seconds, List<(string Mp3, double Offset)> sounds, string output, bool fadeIn = true, bool fadeOut = true)
    {
        var png = Path.Combine(_build, $"{name}.png");
        Brand.Save(image, png);
        image.Dispose();
        List<string> fades = [];
        if (fadeIn)
        {
            fades.Add("fade=t=in:st=0:d=0.4");
        }

        if (fadeOut)
        {
            fades.Add($"fade=t=out:st={Media.Seconds(seconds - 0.4)}:d=0.4");
        }

        fades.Add("format=yuv420p");
        List<string> args = ["-loop", "1", "-framerate", s_fps, "-t", Media.Seconds(seconds), "-i", png];
        var (audioArgs, audioFilters) = Mix(sounds, 1, seconds);
        args.AddRange(audioArgs);
        args.AddRange(["-filter_complex", string.Join(";", [$"[0:v]{string.Join(",", fades)}[vout]", .. audioFilters]), "-map", "[vout]", "-map", "[aout]"]);
        Media.FfmpegRun([.. args, .. s_masterVideo, .. s_masterAudio, "-t", Media.Seconds(seconds), output]);
    }

    private sealed record Frame(double T, string File);

    /// <summary>The recorded frames between start and end, each shown until the next one: (path, seconds).</summary>
    private static List<(string Path, double Seconds)> FrameEntries(string directory, List<Frame> frames, double start, double end)
    {
        var entries = new List<(string, double)>();
        for (var index = 0; index < frames.Count; index++)
        {
            var next = index + 1 < frames.Count ? frames[index + 1].T : end;
            var (a, b) = (Math.Max(frames[index].T, start), Math.Min(next, end));
            if (b > a)
            {
                entries.Add((Path.Combine(directory, "frames", frames[index].File), b - a));
            }
            else if (frames[index].T <= start && start < next)
            {
                entries.Add((Path.Combine(directory, "frames", frames[index].File), 0.001));
            }
        }

        return entries;
    }

    private string WriteConcat(string name, List<(string Path, double Seconds)> entries)
    {
        var concat = Path.Combine(_build, $"{name}.frames.txt");
        var text = new StringBuilder();
        foreach (var (path, seconds) in entries)
        {
            text.Append(Media.ConcatEntry(path)).Append('\n').Append("duration ").Append(seconds.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\n');
        }

        // The concat demuxer ignores the duration of the last entry: repeating it keeps that entry on screen.
        text.Append(Media.ConcatEntry(entries[^1].Path)).Append('\n');
        File.WriteAllText(concat, text.ToString());
        return concat;
    }

    /// <summary>
    /// The frames of a recording with zooms: one entry per output frame, zoomed here with sub-pixel precision
    /// (ffmpeg's zoompan rounds the crop to whole pixels, which shakes), or the recorded frame itself while there's
    /// no zoom.
    /// </summary>
    private List<(string Path, double Seconds)> ZoomedFrames(string clipId, string directory, JsonObject timeline, List<Frame> frames, double start, double end)
    {
        using var first = SKBitmap.Decode(Path.Combine(directory, "frames", frames[0].File));
        var (width, height) = ((double)first.Width, (double)first.Height);
        var viewport = timeline["viewport"]?.AsArray();
        var factor = width / ((double?)viewport?[0] ?? 1536);

        // The target of each zoom: a scale, and a center that keeps the view inside the page.
        var events = new List<(double T, (double Scale, double X, double Y) Target)>();
        foreach (var zoom in timeline["zooms"]!.AsArray().OrderBy(zoom => (double)zoom!["t"]!))
        {
            var target = (1.0, width / 2, height / 2);
            if (zoom!["box"] is JsonArray box)
            {
                var (x, y, w, h) = ((double)box[0]! * factor, (double)box[1]! * factor, (double)box[2]! * factor, (double)box[3]! * factor);
                var scale = Math.Min(Math.Min((double?)zoom["scale"] ?? 1.25, ZoomMax), Math.Min(ZoomFill * width / Math.Max(w, 1), ZoomFill * height / Math.Max(h, 1)));
                if (scale >= ZoomMin)
                {
                    var (halfW, halfH) = (width / scale / 2, height / scale / 2);
                    target = (scale, Math.Clamp(x + w / 2, halfW, width - halfW), Math.Clamp(y + h / 2, halfH, height - halfH));
                }
            }

            events.Add(((double)zoom["t"]!, target));
        }

        var merged = events.Where((item, index) =>
            !(item.Target.Scale == 1.0 && index + 1 < events.Count && events[index + 1].Target.Scale > 1.0 && events[index + 1].T - item.T < ZoomMerge)).ToList();

        static (double, double, double) Blend((double, double, double) a, (double, double, double) b, double progress)
        {
            var eased = progress * progress * (3 - 2 * progress);
            return (a.Item1 + (b.Item1 - a.Item1) * eased, a.Item2 + (b.Item2 - a.Item2) * eased, a.Item3 + (b.Item3 - a.Item3) * eased);
        }

        (double Scale, double X, double Y) State(double t)
        {
            (double, double, double) current = (1.0, width / 2, height / 2);
            (double, double, double)? origin = null;
            (double, double, double) target = current;
            var since = 0.0;
            foreach (var (eventT, eventTarget) in merged)
            {
                if (eventT > t)
                {
                    break;
                }

                if (origin is { } from)
                {
                    current = Blend(from, target, Math.Min(1.0, (eventT - since) / ZoomSeconds));
                }

                (origin, target, since) = (current, eventTarget, eventT);
            }

            return origin is { } start ? Blend(start, target, Math.Min(1.0, (t - since) / ZoomSeconds)) : current;
        }

        var zoomDirectory = Directory.CreateDirectory(Path.Combine(_build, $"{clipId}.zoom")).FullName;
        var entries = new List<(string, double)>();
        var cache = new Dictionary<(string, double, double, double), string>();
        var sourceIndex = 0;
        var count = (int)Math.Round((end - start) * Brand.Fps);
        using var sampling = new SKPaint { IsAntialias = true };

        for (var index = 0; index < count; index++)
        {
            var t = start + index / (double)Brand.Fps;
            while (sourceIndex + 1 < frames.Count && frames[sourceIndex + 1].T <= t)
            {
                sourceIndex++;
            }

            var source = frames[sourceIndex].File;
            var (scale, cx, cy) = State(t);
            if (scale < 1.001)
            {
                entries.Add((Path.Combine(directory, "frames", source), 1.0 / Brand.Fps));
                continue;
            }

            var key = (source, Math.Round(scale, 4), Math.Round(cx, 1), Math.Round(cy, 1));
            if (!cache.TryGetValue(key, out var path))
            {
                using var image = SKBitmap.Decode(Path.Combine(directory, "frames", source));
                using var zoomed = new SKBitmap(image.Width, image.Height);
                using (var canvas = new SKCanvas(zoomed))
                {
                    canvas.Scale((float)scale);
                    canvas.Translate((float)-(cx - width / scale / 2), (float)-(cy - height / scale / 2));
                    using var picture = SKImage.FromBitmap(image);
                    canvas.DrawImage(picture, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell), sampling);
                }

                path = Path.Combine(zoomDirectory, $"{index:000000}.jpg");
                Brand.Save(zoomed, path, jpegQuality: 92);
                cache[key] = path;
            }

            entries.Add((path, 1.0 / Brand.Fps));
        }

        return entries;
    }

    /// <summary>A recording in the frame, with the narration of each step at the time the recorder started it.</summary>
    private (double Seconds, List<(JsonNode Step, double Offset)> Offsets) Recording(string clipId, string chapterTitle, string? label, List<JsonNode> steps, string output)
    {
        var directory = Path.Combine(_workspace, "clips", clipId);
        var timeline = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "timeline.json")))!.AsObject();
        var recorded = timeline["steps"]!.AsArray().ToDictionary(step => (string)step!["id"]!, step => (double)step!["t"]!);
        var missing = steps.Select(step => (string)step["id"]!).Where(id => !recorded.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new VideoException($"{clipId} has no recording of {string.Join(", ", missing)}.");
        }

        var last = steps[^1];
        var start = Math.Max(0.0, recorded[(string)steps[0]["id"]!] - ClipLead);
        var narrationEnd = recorded[(string)last["id"]!] + Duration(last);
        var end = Math.Max(Math.Min((double)timeline["end"]!, narrationEnd + ClipTail), narrationEnd + 0.6);
        var seconds = end - start;

        var frames = timeline["frames"]!.AsArray().Select(frame => new Frame((double)frame!["t"]!, (string)frame["file"]!)).ToList();
        var zooms = timeline["zooms"]?.AsArray();
        var entries = zooms is { Count: > 0 } && ((bool?)_storyboard["zoom"] ?? true)
            ? ZoomedFrames(clipId, directory, timeline, frames, start, end)
            : FrameEntries(directory, frames, start, end);
        if (entries.Count == 0)
        {
            throw new VideoException($"{clipId}: no frames.");
        }

        var concat = WriteConcat(clipId, entries);

        // The recording in the purple frame, under the header; or, without branding, filling the whole frame.
        var framePng = Path.Combine(_build, $"{clipId}.frame.png");
        using (var frame = _branded ? Brand.Frame(chapterTitle, label) : Brand.NewImage(SKColors.Black))
        {
            Brand.Save(frame, framePng);
        }

        var (w, h, x, y) = _branded ? (Brand.VideoW, Brand.VideoH, Brand.VideoX, Brand.VideoY) : (Brand.W, Brand.H, 0, 0);
        List<string> args = ["-loop", "1", "-framerate", s_fps, "-t", Media.Seconds(seconds), "-i", framePng, "-f", "concat", "-safe", "0", "-i", concat];
        List<string> filters =
        [
            $"[1:v]scale={w}:{h}:flags=lanczos,fps={s_fps},setpts=PTS-STARTPTS[rec]",
            $"[0:v][rec]overlay={x}:{y}:shortest=0,trim=0:{Media.Seconds(seconds)}," +
            $"fade=t=in:st=0:d=0.35,fade=t=out:st={Media.Seconds(seconds - 0.35)}:d=0.35,format=yuv420p[vout]",
        ];
        var offsets = steps.Select(step => (step, recorded[(string)step["id"]!] - start)).ToList();
        var sounds = _narrated ? offsets.Select(item => (Audio(item.step), item.Item2)).ToList() : [];
        var (audioArgs, audioFilters) = Mix(sounds, 2, seconds);
        args.AddRange(audioArgs);
        args.AddRange(["-filter_complex", string.Join(";", filters.Concat(audioFilters)), "-map", "[vout]", "-map", "[aout]"]);
        Media.FfmpegRun([.. args, .. s_masterVideo, .. s_masterAudio, "-t", Media.Seconds(seconds), output]);
        return (seconds, offsets);
    }

    /// <summary>Renders the slides of the storyboard to shots/slides/&lt;step&gt;.png, to check them before assembling.</summary>
    public void RenderSlides(string[] only)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_workspace, "shots", "slides")).FullName;
        foreach (var (chapter, clip, label) in Clips())
        {
            if ((string)clip["kind"]! != "slide")
            {
                continue;
            }

            foreach (var step in clip["steps"]!.AsArray())
            {
                var id = (string)step!["id"]!;
                if (only.Length > 0 && !only.Contains(id))
                {
                    continue;
                }

                using var image = Slides.Render(SlideOf(step), (string)chapter["title"]!, label, _workspace);
                var path = Path.Combine(directory, $"{id}.png");
                Brand.Save(image, path);
                Console.WriteLine(path);
            }
        }
    }

    private static JsonObject SlideOf(JsonNode step)
        => step["slide"]?.AsObject() ?? throw new VideoException($"Slide step {(string)step["id"]!} has no \"slide\".");

    /// <summary>The clips of the storyboard, with their chapter, and the chapter's label ("Chapter 3") if numbered.</summary>
    private IEnumerable<(JsonObject Chapter, JsonObject Clip, string? Label)> Clips()
    {
        foreach (var chapter in _storyboard["chapters"]!.AsArray().Select(chapter => chapter!.AsObject()))
        {
            foreach (var clip in chapter["clips"]!.AsArray())
            {
                yield return (chapter, clip!.AsObject(), Label(chapter));
            }
        }
    }

    // Chapters made only of cards, the opening and the closing, are not numbered. "chapter": the word for "Chapter"
    // in the language of the video.
    private string? Label(JsonObject chapter)
        => chapter["clips"]!.AsArray().Any(clip => (string)clip!["kind"]! != "card")
            ? $"{(string?)_storyboard["chapter"] ?? "Chapter"} {int.Parse((string)chapter["id"]!, CultureInfo.InvariantCulture)}"
            : null;

    public void Run()
    {
        Media.CheckFfmpeg();
        var segments = new List<string>();
        var captions = new List<(double Start, double End, string Text)>();
        var clock = 0.0;

        void Caption(string? text, double start, double length)
        {
            if (_narrated && !string.IsNullOrEmpty(text))
            {
                captions.AddRange(Chunks(text, start, length).Select(chunk => (clock + chunk.Start, clock + chunk.End, chunk.Text)));
            }
        }

        foreach (var chapter in _storyboard["chapters"]!.AsArray().Select(chapter => chapter!.AsObject()))
        {
            var (title, subtitle, id) = ((string)chapter["title"]!, (string?)chapter["subtitle"] ?? "", (string)chapter["id"]!);
            var label = Label(chapter);
            // "chapterCards": false leaves out the chapter cards; the header's pill still numbers the chapter.
            if (label is not null && ((bool?)_storyboard["chapterCards"] ?? true))
            {
                var path = Path.Combine(_build, $"card-{id}.mp4");
                // "chapterCardSeconds": a shorter (or longer) chapter card for this video only.
                var cardSeconds = (double?)_storyboard["chapterCardSeconds"] ?? ChapterCardSeconds;
                Still(Card(title, subtitle, label), $"card-{id}", cardSeconds, [], path);
                segments.Add(path);
                clock += cardSeconds;
            }

            foreach (var clip in chapter["clips"]!.AsArray().Select(clip => clip!.AsObject()))
            {
                var clipId = (string)clip["id"]!;
                Console.WriteLine($"encoding {clipId}");
                var steps = clip["steps"]?.AsArray().Select(step => step!).ToList() ?? [];
                switch ((string)clip["kind"]!)
                {
                    case "card":
                    {
                        var step = steps[0];
                        var path = Path.Combine(_build, $"{clipId}.mp4");
                        var seconds = CardLead + Duration(step) + CardTail;
                        Still(Card(title, subtitle, big: true), clipId, seconds, Sounds(step, CardLead), path);
                        Caption((string?)step["text"], CardLead, Duration(step));
                        segments.Add(path);
                        clock += seconds;
                        break;
                    }
                    case "slide":
                        for (var index = 0; index < steps.Count; index++)
                        {
                            var step = steps[index];
                            var (first, last) = (index == 0, index == steps.Count - 1);
                            var path = Path.Combine(_build, $"slide-{(string)step["id"]!}.mp4");
                            var lead = first ? SlideLead : SlideGap / 2;
                            var seconds = lead + Duration(step) + (last ? SlideTail : SlideGap);
                            Still(Slides.Render(SlideOf(step), title, label, _workspace), $"slide-{(string)step["id"]!}", seconds, Sounds(step, lead), path, fadeIn: first, fadeOut: last);
                            Caption((string?)step["text"], lead, Duration(step));
                            segments.Add(path);
                            clock += seconds;
                        }

                        break;
                    case "clip":
                    {
                        var path = Path.Combine(_build, $"{clipId}.mp4");
                        var (seconds, offsets) = Recording(clipId, title, label, steps, path);
                        foreach (var (step, offset) in offsets)
                        {
                            Caption((string?)step["text"], offset, Duration(step));
                        }

                        segments.Add(path);
                        clock += seconds;
                        break;
                    }
                    default:
                        throw new VideoException($"{clipId}: unknown kind \"{(string)clip["kind"]!}\"; use card, slide or clip.");
                }
            }
        }

        var concat = Path.Combine(_build, "segments.txt");
        File.WriteAllLines(concat, segments.Select(Media.ConcatEntry));
        var master = Path.Combine(_build, "master.mp4");
        Media.FfmpegRun(["-f", "concat", "-safe", "0", "-i", concat, "-c", "copy", master]);

        var output = (string)_storyboard["output"]!;
        var names = _storyboard["renditions"]?.AsArray().Select(name => (string)name!).ToList() ?? ["web"];
        var unknown = names.Where(name => !Renditions.ContainsKey(name)).ToList();
        if (unknown.Count > 0)
        {
            throw new VideoException($"Unknown renditions {string.Join(", ", unknown)}: use {string.Join(", ", Renditions.Keys)}.");
        }

        var outputs = new List<(string Name, string Path)>();
        foreach (var name in names)
        {
            var path = Path.Combine(_workspace, name == "web" ? $"{output}.mp4" : $"{output}-{name}.mp4");
            Console.WriteLine($"compressing {name}");
            Publish(master, path, name);
            outputs.Add((name, path));
        }

        // A video without narration has no captions: remove those of an earlier, narrated build, so they don't get
        // delivered with the silent video.
        var baseName = Path.Combine(_workspace, output);
        if (!_narrated)
        {
            File.Delete($"{baseName}.srt");
            File.Delete($"{baseName}.vtt");
        }
        else
        {
            var srt = new StringBuilder();
            var vtt = new StringBuilder("WEBVTT\n\n");
            for (var index = 0; index < captions.Count; index++)
            {
                var (start, end, text) = captions[index];
                srt.Append(CultureInfo.InvariantCulture, $"{index + 1}\n{Timestamp(start)} --> {Timestamp(end)}\n{text}\n\n");
                // WebVTT cues are markup: escape the text, so "List<T>" shows as written.
                var cue = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
                vtt.Append(CultureInfo.InvariantCulture, $"{Timestamp(start, '.')} --> {Timestamp(end, '.')}\n{cue}\n\n");
            }

            File.WriteAllText($"{baseName}.srt", srt.ToString());
            File.WriteAllText($"{baseName}.vtt", vtt.ToString());
        }

        Console.WriteLine($"{clock / 60:0.0} minutes, {segments.Count} segments");
        foreach (var (name, path) in outputs)
        {
            var size = new FileInfo(path).Length;
            var limit = Renditions[name].Limit;
            Console.WriteLine($"{path}: {name}, {size / 1_000_000.0:0.0} MB");
            if (limit is not null && size >= limit)
            {
                var variable = name == "web" ? "CRF" : $"CRF_{name.ToUpperInvariant()}";
                Console.WriteLine($"  Over {limit / 1_000_000} MB: raise {variable} one step ({variable}={Crf(name) + 1}), or shorten the video.");
            }
        }
    }

    private static int Crf(string name)
    {
        var value = Environment.GetEnvironmentVariable($"CRF_{name.ToUpperInvariant()}") ?? (name == "web" ? Environment.GetEnvironmentVariable("CRF") : null);
        return value is { Length: > 0 } ? int.Parse(value, CultureInfo.InvariantCulture) : Renditions[name].Crf;
    }

    /// <summary>A published encode: H.264 tuned for screen content, AAC speech, fast start, at the rendition's size.</summary>
    private static void Publish(string master, string output, string name)
    {
        var rendition = Renditions[name];
        List<string> scale = rendition.Width == Brand.W && rendition.Height == Brand.H ? [] : ["-vf", $"scale={rendition.Width}:{rendition.Height}:flags=lanczos"];
        Media.FfmpegRun(["-i", master, .. scale, "-c:v", "libx264", "-preset", "slow", "-crf", Crf(name).ToString(CultureInfo.InvariantCulture), "-tune", "stillimage",
            "-pix_fmt", "yuv420p", "-r", s_fps, "-g", (Brand.Fps * 10).ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", rendition.Bitrate, "-ar", "48000", "-ac", rendition.Channels.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", output]);
    }

    private static string Timestamp(double seconds, char separator = ',')
    {
        var ms = (long)Math.Round(seconds * 1000);
        return $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}";
    }

    /// <summary>Splits a narration into captions of at most about 13 words, timed by their share of the characters.</summary>
    private static IEnumerable<(double Start, double End, string Text)> Chunks(string text, double start, double length)
    {
        var parts = new List<string>();
        foreach (var sentence in SentenceEnd().Split(text))
        {
            var words = sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            while (words.Count > 0)
            {
                var take = words.Take(13).ToList();
                words = words.Skip(13).ToList();
                // A short tail joins the previous caption rather than standing alone.
                if (words.Count < 4)
                {
                    take.AddRange(words);
                    words.Clear();
                }

                parts.Add(string.Join(' ', take));
            }
        }

        var total = parts.Sum(part => part.Length + 6.0);
        var t = start;
        foreach (var part in parts)
        {
            var share = length * (part.Length + 6) / total;
            yield return (t, t + share, part);
            t += share;
        }
    }

    [GeneratedRegex(@"(?<=[.!?:])\s+")]
    private static partial Regex SentenceEnd();

    /// <summary>
    /// review &lt;workspace&gt; &lt;clip&gt; [seconds...]: a contact sheet of a recorded clip, to check it without
    /// watching it: the frame 2 seconds into each step, the frame at the end of its narration, and the frames at the
    /// given times; or the first and last frames. Writes shots/&lt;clip&gt;.jpg in the workspace.
    /// </summary>
    public static void Review(string workspace, string clip, double[] extra)
    {
        var directory = Path.Combine(workspace, "clips", clip);
        var timeline = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "timeline.json")))!;
        var durationsPath = Path.Combine(workspace, "durations.json");
        var durations = File.Exists(durationsPath) ? JsonNode.Parse(File.ReadAllText(durationsPath))!.AsObject() : [];
        var frames = timeline["frames"]!.AsArray().Select(frame => ((double)frame!["t"]!, (string)frame["file"]!)).ToList();

        string At(double t) => frames.LastOrDefault(frame => frame.Item1 <= t, frames[0]).Item2;

        var times = new List<(string Label, double T)>();
        foreach (var step in timeline["steps"]!.AsArray())
        {
            var (id, t) = ((string)step!["id"]!, (double)step["t"]!);
            times.Add(($"{id} +2s", t + 2));
            times.Add(($"{id} end", t + ((double?)durations[id] ?? 2)));
        }

        times.AddRange(extra.Select(t => ($"t={t.ToString(CultureInfo.InvariantCulture)}", t)));
        if (times.Count == 0)
        {
            times = [("first frame", frames[0].Item1), ("last frame", frames[^1].Item1)];
        }

        const int Columns = 2, ThumbW = 768, ThumbH = 432, CellH = 460;
        using var sheet = new SKBitmap(ThumbW * Columns, CellH * ((times.Count + Columns - 1) / Columns));
        using (var canvas = new SKCanvas(sheet))
        {
            canvas.Clear(SKColors.White);
            using var font = Brand.Font(16);
            for (var index = 0; index < times.Count; index++)
            {
                var (label, t) = times[index];
                using var image = SKBitmap.Decode(Path.Combine(directory, "frames", At(t)));
                var (x, y) = (index % Columns * ThumbW, index / Columns * CellH);
                var scale = Math.Min(ThumbW / (float)image.Width, ThumbH / (float)image.Height);
                using var picture = SKImage.FromBitmap(image);
                canvas.DrawImage(picture, SKRect.Create(x, y + 28, image.Width * scale, image.Height * scale), new SKSamplingOptions(SKCubicResampler.Mitchell));
                Brand.DrawText(canvas, label, x + 6, y + 14, font, SKColors.Black);
            }
        }

        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(workspace, "shots")).FullName, $"{clip}.jpg");
        Brand.Save(sheet, output, jpegQuality: 80);
        Console.WriteLine(output);
    }
}
