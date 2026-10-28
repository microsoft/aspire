---
name: create-video
description: >-
  Produces narrated Aspire videos (feature walkthroughs, dashboard tours, integration overviews, pull request demos)
  with one unified look and specification - 1080p, the Aspire logo and purple palette from aspire.dev, a neural voice
  picked from a shortlist, captions, slight dynamic zooms on demos, and renditions for GitHub, aspire.dev, YouTube or
  chat apps. Use when asked to record, narrate, or produce a video of Aspire, its dashboard or its CLI: narrated and
  branded by default, or silent or unbranded when the user asks for a simpler video.
---

# Create an Aspire video

Every Aspire video looks the same: the same branding, layout, captions, zooms, and encodings. The user picks the
narrator, the renditions, and whether sections get chapter cards (step 0).
This skill holds that specification and the scripts that apply it. It's adapted from the Orchard Core video producer
skill (BSD 3-Clause License, see "Orchard Core" in the repository's `THIRD-PARTY-NOTICES.TXT`). **Don't change the specification for a single
video.** If it must change, change it here, in `scripts/Brand.cs`, `scripts/Speech.cs` and `scripts/Assembly.cs`,
and in `references/specs.md`, so every later video follows.

## When to use it

By default, a video is narrated and branded: walkthroughs, overviews, tours and demos with a voice, captions, cards
and slides. A simpler video turns those off, in the storyboard:

- **No narration** (`"narration": false`): a silent video, without captions; each step lasts its `"seconds"`.
- **No branding** (`"branding": false`): no Aspire logo and no Aspire color. Recordings fill the whole frame, cards
  are white text on black, and the recorder's highlight rings, click ripples and captions are white and black.

For a simpler video, ask only what it needs (usually the renditions), skip the voice and chapter-card questions, and
don't add cards, slides or narration the user didn't ask for. Any other layout, such as two runs side by side, comes
from the user's request: build it for that video (for instance with ffmpeg, from recordings made with this skill's
recorder), rather than from a preset. When it isn't clear what the user wants, ask.

The worked example in `examples/dashboard-tour/` is the storyboard and slides of a short tour of the Aspire
dashboard, matching the example clip in `scripts/recorder/Scenes.cs`. Read it before writing a new one.

## The specification, in short

| | |
|---|---|
| Output | 1920x1080, 30 fps, H.264 (`-tune stillimage`), `+faststart`, in the renditions the user picks: `web` (1080p, **under 10 MB**, the default), `hd` (1080p, best quality), `light` (720p, under 5 MB) |
| Recording | Chromium at 1536x864 CSS pixels, device scale 1.25, dark color scheme, en-US, shown at 1728x972 in a purple frame |
| Branding | The aspire.dev dark theme (`#1F1E33`) with purple and magenta glows, the Aspire logo (symbol and white "Aspire" name), the system UI and monospace fonts (Segoe UI and Cascadia Mono on Windows, Helvetica Neue and Menlo on macOS) |
| Colors | Purple `#512BD4` (pills), primary `#7455DD` (accents, frame, highlights, cursor clicks), secondary `#B9AAEE`, muted `#DCD5F6`, black `#1F1E33`, white |
| Voice | The narrator the user picks from a shortlist (default `en-US-AndrewMultilingualNeural`), rate `+2%`, through the `msedge-tts` npm package (`scripts/tts/tts.mjs`) |
| Structure | Opening card, numbered chapters (an optional chapter card + slides or recordings), closing card |
| Zooms | Slight (1.25x), eased zooms on what each recorded step points at, rendered by the assembly |
| Captions | WebVTT and SRT, from the narration, at most about 13 words each |

The full specification, with every value and the reasons behind them, is in `references/specs.md`.

## Prerequisites

- .NET SDK 10.0.300 or later: the tools (`scripts/video.cs`) and the recorder (`scripts/recorder/recorder.cs`, with
  Playwright for .NET) are .NET file-based apps, split with `#:include`. Install the recorder's browser once: `dotnet
  run <tools>/recorder/recorder.cs -- install` (the Chromium build of the recorder's Playwright version).
- Node.js 22 or later (24 LTS recommended), for the voice. Its package, `msedge-tts`, is pinned in `scripts/tts/package.json` and
  `package-lock.json`, and installed from the lockfile: `npm ci --prefix <tools>/tts`. Microsoft changes the speech
  service's handshake from time to time: when every request fails with a 403, update `msedge-tts` (Dependabot
  proposes new versions).
- ffmpeg 7 or later, with the libx264 encoder, on the `PATH` (or named by `FFMPEG`): `winget install Gyan.FFmpeg`,
  `brew install ffmpeg`, or `sudo apt install ffmpeg`.
- The Aspire CLI and a throwaway sample app to record: see `references/recording.md`.
- Internet access for the voice (`msedge-tts` calls the Microsoft Edge speech service).
- Docker (or Podman) running, when the app to record has container resources.

Check these first. If one is missing, tell the user what to install rather than working around it.

`<skill>` below is the folder of this skill, and `<tools>` the copy of its `scripts` folder in the workspace (step 1).
Every package the tools use is pinned to an exact version: the NuGet packages in `scripts/Directory.Packages.props`
(central package management, for both file-based apps), the npm packages in `scripts/tts/package-lock.json`. Nothing
installs a missing prerequisite on its own: the tools stop and say what to install. Run the tools with `dotnet run <tools>/video.cs -- <command> ...`; the first run
builds them (a few seconds), and later runs reuse the build.

## Workflow

### 0. Understand the request

The user describes what the video is about; everything else comes from this skill. Work out:

- **The subject**: a feature, an integration, the dashboard, the CLI, or the change of a pull request.
- **The kind**: an overview (4 to 9 minutes, the outline of `references/storyboard.md`) or a pull request demo
  (shorter: an opening card, the recordings, a closing card).
- **What to show**: the app or the pull request branch to record, and the code for the slides.
- **Which Aspire runs it**: the installed `aspire` CLI (the default), a released version, or the current branch of
  the Aspire repository, to show a feature still in development. See "Which Aspire runs the app" in
  `references/recording.md`. Ask when the subject is new or unreleased: recording it needs the current branch.
- **Where it goes**: aspire.dev, a pull request, or only the file.

Ask only about what you can't infer. Then ask the user these three, together, as multiple-choice questions (with the
question tool when the session has one), unless they already said:

1. **Renditions** (several can be picked): `web`, 1080p under 10 MB, for GitHub pull requests and aspire.dev (the
   default); `hd`, 1080p at the best quality, for YouTube, conferences or editing; `light`, 720p under 5 MB, for
   Teams, Slack, email or phones. Recommend from where the video goes. See "Renditions" in `references/specs.md`;
   there is no vertical or 4K rendition.
2. **Voice**: the shortlist of "Voice" in `references/specs.md`, Andrew (the default) first. Offer a sample before
   they choose: `dotnet run <tools>/video.cs -- voices <workspace>/voices.mp4` reads one line in each voice of the
   shortlist (or in the voices given), on cards that name them. Send it, and use the voice they pick. For another
   voice or language, pick from `dotnet run <tools>/video.cs -- voices --list <prefix>` (such as `es-`): only the voices
   it lists work.
3. **Chapter cards**: a card between sections ("Chapter 2, The resources"), or none, for a quicker video. Recommend
   cards for overviews of 3 minutes or more, none for short videos and pull request demos.

Write the answers into the storyboard: `renditions`, `voice`, `chapterCards`. To test the skill itself, produce
`examples/dashboard-tour` as it is.

### 1. Create a workspace outside any repository

Use the session's scratchpad or a temp folder, never a repository: frames and audio are large, and the tools must not
pick up a repository's build settings or package sources. Copy the skill's `scripts` folder into it as `tools`,
install the voice package, and work from the workspace:

```bash
cp -R <skill>/scripts <workspace>/tools
npm ci --prefix <workspace>/tools/tts
cd <workspace>
```

**Run every command of the steps below from the workspace.** The .NET CLI picks its SDK from the `global.json`
nearest the current directory, not the script's: from inside a repository, such as the Aspire checkout (which pins a
preview SDK), `dotnet run <tools>/...` would use that repository's SDK, or fail without it.

```text
<workspace>/
  storyboard.json   the chapters, clips, narration and slides (see references/storyboard.md)
  slides/           the code shown on slides, if any ("source" of a code panel)
  tools/            a copy of the skill's scripts: video.cs, tts/, and recorder/ with your scenes in Scenes.cs
  app/              the throwaway Aspire app to record, if the video shows one
  audio/            generated: one mp3 per step
  durations.json    generated: the length of each step's narration
  clips/<clip>/     generated: the frames and timeline.json of each recording
  build/            generated: intermediate segments, and master.mp4
  <output>.mp4/.vtt/.srt   the result
```

Start from `examples/dashboard-tour/storyboard.json` and its `slides/` folder.

### 2. Write the storyboard

Read `references/storyboard.md` first. In short:

- Write for the long term: say "the Aspire dashboard", never "the new dashboard", "now", "in this release", or version
  numbers, so the video stays accurate for years.
- One idea per step, one to three sentences, spoken in the present tense. A step is what the viewer sees while it plays.
- Chapters: why it exists, each feature, how to configure it in the AppHost, how to adopt or extend it.

Show the user the outline and the narration before generating anything, unless they asked you to go straight
through: changing the text after recording means recording again.

### 3. Generate the narration

```bash
dotnet run <tools>/video.cs -- narrate <workspace>
```

It writes `audio/` and `durations.json`. Run it again after any change to the text: only the changed steps are
generated again. **Record after narrating**, since the recorder holds each step for the length of its narration.

If the voice mispronounces a word, add it to `s_spoken` in `scripts/Speech.cs` (for every video) or to `"spoken"` in
the storyboard (for this one). The captions keep the written form. **Have a person confirm a new spelling before
using it**: make a pronunciation sample, a few seconds per candidate spelling, and ask which option sounds right.
Rebuilding a whole video to try a spelling wastes minutes per attempt. Pass the storyboard, so the sample uses the
video's voice and rate (or `--voice` and `--rate`): a spelling that works for one voice can fail with another.

```bash
dotnet run <tools>/video.cs -- pronounce --storyboard <workspace>/storyboard.json otlp.mp4 "The dashboard receives {} telemetry from every resource." "OTLP" "O.T.L.P." "O-T-L-P" "O T L P"
```

Letters said one by one are written as an abbreviation with periods, such as "A.S.P.": the voice spells it at an
even pace. With spaces ("A S P"), the letters run together; with commas ("A, S, P"), the pauses are uneven.
"ASP.NET" is spoken as "A.S.P. .NET".

### 4. Record the clips

Start the sample app (`aspire run`), then write one method per "clip" in `tools/recorder/Scenes.cs`, with one
`scene.StepAsync("<step id>", ...)` per step, in the storyboard's order. See `references/recording.md` for the app
to record, the scene API, and the pitfalls. Wait until every resource is Running and healthy, and generate some
traffic so logs, traces and metrics have data. Then:

Each workspace's copy of the recorder builds into its own folder, so several videos can be recorded side by side.

```bash
export VIDEO_ROOT=<workspace> VIDEO_BASE_URL=https://localhost:17010
export VIDEO_LOGIN_URL='<the dashboard login URL that aspire run prints>'
dotnet run <tools>/recorder/recorder.cs -- setup          # logs in to the dashboard, once per run of the app
dotnet run <tools>/recorder/recorder.cs -- probe / "..."  # checks each selector of Scenes.cs before recording
dotnet run <tools>/recorder/recorder.cs -- record all     # or the names of some clips
dotnet run <tools>/video.cs -- review <workspace> <clip>
```

Look at every contact sheet (`shots/<clip>.jpg`) before assembling: the frame at the end of each step must show what
its narration describes, and no caption may cover what the narration names.

To make several versions of one video (30 seconds, 1 minute, 3 minutes), use one workspace per version, with its own
storyboard, slides and recorder, and record them all from the same running app.

### 5. Assemble

```bash
dotnet run <tools>/video.cs -- slides <workspace>     # renders the slides to shots/slides/, to check them first
dotnet run <tools>/video.cs -- assemble <workspace>
```

It writes one file per rendition (`<output>.mp4` for `web`, `<output>-hd.mp4`, `<output>-light.mp4`), and prints
the length and the size of each. If one is over its limit, shorten the video, or raise its CRF one step at a time
(`CRF=34` for `web`, `CRF_LIGHT=31` for `light`) and check that text stays readable. Extract a few frames with ffmpeg
and look at them: a recording, a slide with code, a card, and a recording while it's zoomed in.

### 6. Publish

- **aspire.dev** (`microsoft/aspire.dev`, an Astro Starlight site): put `<name>.mp4`, `<name>.vtt` and a poster
  (`build/<opening card>.png` saved as JPEG) in `src/frontend/public/videos/`, and embed it under the introduction of
  the `.mdx` page:

  ```html
  <video controls preload="metadata" width="100%" poster="/videos/<name>.jpg">
      <source src="/videos/<name>.mp4" type="video/mp4" />
      <track kind="captions" src="/videos/<name>.vtt" srclang="en" label="English" />
      Your browser does not support embedded videos. <a href="/videos/<name>.mp4">Download the video</a>.
  </video>
  ```

  Don't use the `LoopingVideo` component for narrated videos: it autoplays muted, without captions. Introduce the
  video with one sentence that says what it covers, without "new". Build the site locally, following the
  repository's README, and check the page.
- **Pull request**: GitHub only accepts videos through its web uploader (drag and drop, or the attachment button
  of the description editor), up to 10 MB on free plans. The upload gives a `https://github.com/user-attachments/assets/...`
  URL: put it alone on a line near the top of the description, so GitHub shows a player.

### 7. Clean up

Stop the app you recorded: `aspire stop` from `<workspace>/app`, or Ctrl+C in the terminal running it. An app
started in the background may ignore an interrupt (SIGINT): stop its processes with SIGTERM, and only those whose
command line names its workspace.
`aspire stop` removes the app's containers within about 20 seconds; after that, any left over outlived it: check
`docker ps -a` and remove them (`docker rm -f <name>`, such as `cache-<suffix>`).
Keep the throwaway project and the workspace until step 8 has copied their sources and the user has accepted the
video, in case a step must be re-recorded; remove the project after that.

### 8. Deliver

Give the user `<output>.mp4` (send the file when the session can), and end with a summary that lists, for each video:

- **A link to each rendition**, as a clickable link to its absolute path (`[<output>.mp4](/abs/path/<output>.mp4)`),
  with its length, its resolution, its size, its voice, and its language. Copy the videos and their captions out of a temporary workspace, to a
  folder the user can open (such as `<working directory>/<name>-videos/`), and link to the copies: a temporary folder is
  temporary.
- **Links to its captions**: `<output>.vtt` and `<output>.srt`.
- **The path to its workspace**, with links to `storyboard.json` (the narration and slides), and the
  recorder's `Scenes.cs`, so the user can change the text or a clip and produce the video again. When the workspace
  is in a temporary folder, also copy its sources next to the videos (`<name>-videos/workspaces/`: the storyboards,
  `slides/`, `tools/` without `tts/node_modules`, `bin` and `obj`, and the app without `node_modules`, `bin`, `obj` and
  `aspire-output`), and link to both: the copy outlives the session, the workspace keeps the frames and audio.
- **Shared files**, when several videos use them: the sample app, the recorder, and any settings or AppHost versions
  the clips copy in.

End with the commands that produce a video again from its workspace (`video.cs narrate`, `recorder.cs record`, `video.cs
assemble`).

## Improving the skill

When a script fails, a selector breaks with a newer dashboard, a slide overflows, or a reference is wrong or unclear,
fix it here, in the skill (scripts, references or example), not only in the workspace, so the next video benefits.
When an external tool misbehaves (VS Code, the Aspire CLI, a package, the voice service), add the symptom, the cause and
the fix to `references/troubleshooting.md`.
Tell the user what you changed. Never change the specification (colors, fonts, layout, voice, encoding) without the
user's agreement.

## References

- `references/specs.md`: the full specification.
- `references/storyboard.md`: the storyboard format, and how to write the narration and the slides.
- `references/recording.md`: the app to record, the recorder, the scene API, and pitfalls.
- `references/troubleshooting.md`: problems with external tools (VS Code in the browser, the Aspire CLI, NuGet
  packages, the dashboard, the voice), with their fixes. Check it before debugging one, and add what you find.
- `scripts/video.cs`: the tools, a .NET file-based app: `narrate`, `voices`, `pronounce`, `slides`, `assemble`,
  `review`. `Brand.cs` holds the branding and layout, `Slides.cs` the slides, `Speech.cs` the voice, `Assembly.cs` the
  assembly, and `Media.cs` the calls to ffmpeg and Node.js.
- `scripts/tts/`: the voice (`tts.mjs`), with its pinned npm package.
- `scripts/recorder/`: the recorder, a Playwright for .NET file-based app, with helpers for VS Code in the browser
  (`VsCode.cs`, `vscode-settings.jsonc`) and for the dashboard (`Dashboard.cs`).
- `scripts/assets/`: the Aspire logo, from aspire.dev.
- `cgmanifest.json`: the components the skill uses that component detection can't find: the packages of `video.cs`,
  and Orchard Core, which the skill is adapted from.
- `examples/dashboard-tour/`: a complete storyboard, with slides.
