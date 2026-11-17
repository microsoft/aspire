# The storyboard

`storyboard.json` describes the whole video: its chapters, the clips of each chapter, and the narrated steps of each
clip, and the slides. The tools (`video.cs`) and the recorder all read it.

```json
{
  "title": "The Aspire dashboard",
  "output": "dashboard-tour",
  "voice": "en-US-AndrewMultilingualNeural",
  "renditions": ["web", "light"],
  "chapterCards": true,
  "spoken": { "OTLP": "O.T.L.P." },
  "chapters": [
    {
      "id": "00", "title": "The Aspire dashboard", "subtitle": "See every resource of your app, as it runs",
      "clips": [ { "id": "00-intro", "kind": "card", "steps": [ { "id": "intro", "text": "..." } ] } ]
    },
    {
      "id": "01", "title": "Why the dashboard", "subtitle": "The problem it solves",
      "clips": [ { "id": "01-why", "kind": "slide", "steps": [ { "id": "why-1", "text": "..." } ] } ]
    },
    {
      "id": "02", "title": "The resources", "subtitle": "Everything the AppHost runs",
      "clips": [ { "id": "02-resources", "kind": "clip", "steps": [ { "id": "resources-1", "text": "..." } ] } ]
    }
  ]
}
```

- `output`: the file name of the result, without extension: `<output>.mp4`, `.vtt`, `.srt`.
- `spoken`: pronunciation fixes for this video only (see `s_spoken` in `scripts/Speech.cs` for those of every video).
- `voice`, `rate` (optional): the narrator the user picked (see "Voice" in `references/specs.md`), such as
  `en-US-AvaMultilingualNeural`, or a voice for a video in another language (`es-ES-AlvaroNeural`). Without it, the
  default voice.
- `renditions` (optional): the renditions to encode, from the table of `references/specs.md`: `"web"`, `"hd"`,
  `"light"`. The default is `["web"]`.
- `chapterCards` (optional): `false` for no chapter card between sections. The default is `true`.
- `zoom` (optional): `false` to render the recordings without their dynamic zooms. The default is `true`.
- `narration` (optional): `false` for a silent video, without voice or captions. Each step then lasts its `"seconds"`
  (3 by default), and its `"text"` is optional. `narrate` still runs: it writes `durations.json` from them.
- `branding` (optional): `false` for a video without the Aspire look: no logo and no Aspire color. Recordings fill the
  whole frame, with no header or border; cards are white text on black; the recorder's highlights are white and
  black. Slides are branded: leave them out.
- `chapter` (optional): the word for "Chapter" on chapter cards and pills, in the language of the video ("Capítulo").
- `chapterCardSeconds` (optional): how long each chapter card stays on screen, when the user wants quicker (or
  slower) transitions than the default 2.6 s of `references/specs.md`, such as `1.2`.
- Chapter `id`: two digits. Chapters made only of `card` clips (the opening `00` and the closing `99`) get no
  chapter card; every other chapter is numbered "Chapter N" from its id.
- Clip `kind`:
  - `card`: the opening or closing card, with the chapter's title and subtitle, and one narrated step.
  - `slide`: one image per step, drawn from the step's `"slide"` (see "Writing the slides").
  - `clip`: a recording, made by the method of the same name in `tools/recorder/Scenes.cs`.
- Step `id`: unique in the video; the recorder marks the time each one starts.

## Structure

A feature or integration overview usually follows this outline. Keep it between 4 and 9 minutes.

1. **Opening card** (`00`): what the subject is, in one or two sentences, and what the video covers.
2. **Why** (slides): the problem it solves, then how it solves it.
3. **The features** (recording): where they are in the dashboard, what each one does.
4. **Using it** (recordings): one chapter per task, in the order a developer would do them.
5. **Configuring it** (slides with code): the AppHost code (`AppHost.cs`, or `apphost.ts` for a TypeScript AppHost),
   the client integration in the service, `appsettings.json`, CLI commands.
6. **Deploying or extending it** (slides with code): the few steps a developer follows, with real API names.
7. **Closing card** (`99`): one sentence that sums it up, then "Thanks for watching."; the subtitle is `aspire.dev`.

A pull request demo can be shorter: an opening card, the recordings, and a closing card.

### Short versions

For a video of a given length, add up the timing of `references/specs.md` before recording: each card takes its
narration plus 2.4 s, each chapter card 2.6 s, each recording its steps (narration plus 0.45 s each) plus 1.7 s, and
each slide clip its narration plus about 1.6 s. Run `narrate`, compute the length, and adjust the text until it
fits, then record.

| Length | Outline |
|---|---|
| 30 seconds | Opening card (one sentence), one recorded chapter of two steps, closing card ("Get started at aspire.dev.") |
| 1 minute | Opening card, the AppHost (one code slide), the dashboard (one recording of two or three steps), closing card |
| 3 minutes | The full outline above, one or two steps per chapter |

## Cards

The opening and closing cards show the Aspire logo above the title: don't use "Aspire" alone as the title, or the
card reads "Aspire Aspire". Name what the video is, such as "An introduction to Aspire" or "The Aspire dashboard".

## Writing the narration

- **Timeless.** Say "the Redis integration", never "the new Redis integration", "now", "this release", "we added",
  or a version number. Describe what is, not what changed, so the video stays accurate for years.
- **One idea per step**, one to three sentences, 3 to 16 seconds. The viewer sees the step's actions while it plays,
  so describe what's on screen, as it happens.
- **Plain, direct, present tense.** Address the viewer as "you", or use "let's" for actions in a recording.
- **Name UI elements as they're labeled** on screen, so the viewer can find them: "Resources", "Console",
  "Structured", "Traces", "Metrics".
- **Use Aspire's terms**: "the AppHost", "a resource", "an integration" (hosting or client), "the dashboard", "the
  Aspire CLI". Say "Aspire", not ".NET Aspire".
- **Acronyms**: the voice reads most of them correctly (API, HTTP, SQL). For one that must be spelled out letter by
  letter, add a `spoken` entry that writes it as an abbreviation with periods, such as "A.S.P. .NET" for "ASP.NET",
  and have a person confirm it with a sample from `video.cs pronounce`.
- **Spell out** what a voice could misread: say "Add Redis" for `AddRedis()`, "With Reference" for
  `WithReference()`, "Wait For" for `WaitFor()`. Check every name of the narration for pronunciation, and add fixes
  to `spoken`.
- **No marketing words**: no "powerful", "seamless", "simply", "easily", "magic".
- Avoid numbers that change, such as counts of integrations or settings, unless the video shows them.

## Writing the slides

A slide is data: the `"slide"` of a step of a `slide` clip. The tools draw it in the Aspire look, so every video's
slides match. A heading, then one of: bullet cards, code panels, or code panels with note cards beside them.

```json
{ "id": "why-1", "text": "...",
  "slide": {
    "heading": "Many processes, many consoles",
    "bullets": [
      { "icon": "1", "title": "Many processes", "text": "Projects, containers and executables, each in its own terminal." },
      { "icon": "check", "title": "Every resource", "text": "Its state, its health, and its endpoints, in one list." }
    ],
    "active": [0]
  } },
{ "id": "apphost-1", "text": "...",
  "slide": {
    "heading": "The AppHost describes the app",
    "code": { "title": "AppHost.cs", "language": "csharp", "source": "slides/AppHost.cs", "size": 22,
              "types": ["DistributedApplication", "Projects"] },
    "notes": [ { "title": "Add Redis", "text": "a container resource" } ]
  } }
```

- `bullets`: up to four cards of `icon`, `title` and `text`; `icon` is a short text such as "1", or `"check"`.
  Spread the cards over several steps with `active` (the indexes shown bright, the others dimmed) to follow the
  narration; without `active`, all are bright.
- `code`: a panel, or a list of panels stacked from top to bottom, each as tall as its code. `title` is the file
  name in the window bar; the code is `source` (a file, relative to the workspace, such as `slides/AppHost.cs`) or
  `text`. Show real, compilable code from aspire.dev or the templates, trimmed to what the narration talks about;
  mark what's omitted with `// ...`. `language`: `csharp`, `typescript`, `javascript`, `json`, `yaml`, `bash`,
  `powershell`, `xml`, `html`; the colors are Visual Studio Code's Dark+ theme. `types`: the names to color as types,
  which the grammars can't tell from variables. `highlight`: the 1-based lines shown bright, the others dimmed.
  `size`: the font size, 22 or more (25 by default); keep at most about 20 lines per panel.
- `notes`: up to three cards of `title` and `text`, beside the code: a fourth doesn't fit next to the panel.
- A text that runs off its card, its panel or the slide stops the build with a "Slide layout" error: shorten it, or
  split the slide. Render the slides before assembling, and look at them:
  `dotnet run <tools>/video.cs -- slides <workspace>` writes `shots/slides/<step>.png`.
