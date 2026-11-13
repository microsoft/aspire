#!/usr/bin/env dotnet
// The video tools of the create-video skill: narration, voice and pronunciation samples, slides, assembly and review of a video
// workspace. A .NET file-based app: run it from a copy of this folder outside any repository (see SKILL.md), with
//   dotnet run <tools>/video.cs -- <command> <arguments>
//
// Commands:
//   narrate <workspace> [--force]          speaks each step, and writes durations.json
//   voices <output.mp4> ["line"] [voice...] the same line in each voice of the shortlist (or the voices given)
//   voices --list [prefix]                 the voices the Edge voice service offers, such as "voices --list en-"
//   pronounce [--storyboard <file>] [--voice <voice>] [--rate <rate>] <output.mp4> "line with {}" "written" "spelling"...
//   slides <workspace> [step...]           renders the slides to shots/slides/<step>.png, to check them
//   assemble <workspace>                   assembles the video, its renditions and its captions
//   review <workspace> <clip> [seconds...] writes a contact sheet of a recording to shots/<clip>.jpg
//
// The package versions are in Directory.Packages.props, pinned with the recorder's.
#:package SkiaSharp
#:package SkiaSharp.NativeAssets.Linux
#:package TextMateSharp.Grammars
#:include Brand.cs
#:include Slides.cs
#:include Media.cs
#:include Speech.cs
#:include Assembly.cs

using System.Globalization;
using System.Text.Json.Nodes;

try
{
    switch (args)
    {
        case ["narrate", var workspace, .. var rest]:
            Speech.Narrate(Path.GetFullPath(workspace), rest.Contains("--force"));
            break;
        case ["voices", _, ..]:
            Speech.Voices(args[1..]);
            break;
        case ["pronounce", _, _, _, _, ..]:
            Speech.Pronounce(args[1..]);
            break;
        case ["slides", var workspace, .. var steps]:
            new Assembly(Path.GetFullPath(workspace)).RenderSlides(steps);
            break;
        case ["assemble", var workspace]:
            new Assembly(Path.GetFullPath(workspace)).Run();
            break;
        case ["review", var workspace, var clip, .. var seconds]:
            Assembly.Review(Path.GetFullPath(workspace), clip, [.. seconds.Select(value => double.Parse(value, CultureInfo.InvariantCulture))]);
            break;
        default:
            Console.Error.WriteLine("Usage: dotnet run video.cs -- narrate|voices|pronounce|slides|assemble|review ... (see the top of video.cs)");
            return 2;
    }

    return 0;
}
catch (VideoException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static class Video
{
    public static JsonObject Storyboard(string workspace)
    {
        var path = Path.Combine(workspace, "storyboard.json");
        return File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))!.AsObject()
            : throw new VideoException($"No storyboard.json in {workspace}.");
    }
}
