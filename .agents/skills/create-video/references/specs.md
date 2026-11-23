# The Aspire video specification

Every Aspire video follows these values. They are applied by the tools (`scripts/Brand.cs`, `scripts/Slides.cs`,
`scripts/Speech.cs`, `scripts/Assembly.cs`), the voice (`scripts/tts/tts.mjs`) and the recorder
(`scripts/recorder/Scene.cs`); change them there, and here, never for a single video.

## Output

| Setting | Value | Why |
|---|---|---|
| Resolution | 1920x1080, then scaled per rendition (below) | Full HD: sharp text on any screen. |
| Frame rate | 30 fps | Smooth cursor movement, small files. |
| Video codec | H.264 High, `yuv420p`, `-preset slow -tune stillimage`, the rendition's CRF, keyframe every 10 s | Plays everywhere; screen content compresses well. |
| Audio codec | AAC, 48 kHz, the rendition's bitrate and channels | Speech only: mono is enough, except for the best quality. |
| Container | MP4 with `-movflags +faststart` | Starts playing before it's fully downloaded. |
| Master | `build/master.mp4`, CRF 16, AAC 192 kbps stereo | Kept in the workspace to encode again without rebuilding. |
| Captions | WebVTT (docs) and SRT, at most about 13 words each, split at sentence ends | Accessibility, and viewing without sound. |

### Renditions

The user picks one or more renditions, by where the video goes; the storyboard lists them in `"renditions"`
(`["web"]` by default). Each one is encoded from `build/master.mp4` by the assembly (`Renditions` in `scripts/Assembly.cs`).

| Rendition | File | Use | Resolution | CRF | Audio | Size limit |
|---|---|---|---|---|---|---|
| `web` | `<output>.mp4` | GitHub pull requests and issues, aspire.dev pages | 1920x1080 | 33 | 64 kbps mono | 10 MB: GitHub's attachment limit, and a reasonable size for the aspire.dev repository. An 8-minute video is about 9.7 MB. |
| `hd` | `<output>-hd.mp4` | YouTube, conferences, editing: the best quality | 1920x1080 | 20 | 160 kbps stereo | None |
| `light` | `<output>-light.mp4` | Teams, Slack, email, phones: small and quick to load | 1280x720 | 30 | 64 kbps mono | 5 MB |

The layout is drawn for 16:9 Full HD: there's no vertical (9:16) or square rendition, and no 4K one (it would only be
the 1080p video scaled up). When a rendition is over its limit, raise its CRF one step at a time (`CRF=34` for
`web`, `CRF_LIGHT=31` for `light`) and check that text stays readable, or shorten the video.

## Layout

- **Background**: `#1F1E33` (the aspire.dev dark background), with blurred glows: purple in the top right corner,
  magenta in the bottom left.
- **Header** (recordings and slides): the logo, 190 px wide, at x = 96, centered on y = 47; a thin divider; the
  chapter title in the sans-serif font, semibold, 32 px; on the right, a `#512BD4` pill with the chapter label ("CHAPTER 3") in
  bold 21 px, white.
- **Recordings**: scaled to 1728x972 (90%), centered horizontally, 14 px from the bottom, with a soft shadow and a
  3 px `#7455DD` rounded border.
- **Slides**: a heading in bold 52 px at y = 150, underlined by a 100x6 px `#7455DD` bar; content from
  y = 240, within 96 px margins.
- **Bullet and note cards**: `#2A2949` (dark surface), with a 2 px `#7455DD` border; dimmed cards use `#3B3A5E`.
- **Code panels**: `#161527` with a `#2A2949` window bar (red, yellow and green dots) and the file name; the monospace font,
  22 to 28 px; the syntax colors of the Visual Studio Code dark theme; at most about 20 lines per panel.
- **Cards**: the opening and closing cards show the full logo (640 px) over the video's title, a purple bar, and the
  subtitle. Chapter cards show the symbol (150 px), "CHAPTER N" in `#B9AAEE`, the chapter's title and subtitle. The
  user chooses whether the video has chapter cards between sections (`"chapterCards"`, true by default); without
  them, the header's pill still numbers each chapter.

### Simpler videos

- **No narration** (`"narration": false`): silent audio, no captions; each step lasts its `"seconds"`.
- **No branding** (`"branding": false`): no Aspire logo and no Aspire color. Recordings scaled to the full 1920x1080
  frame, with no header, border or shadow; cards in white (`#FFFFFF`, title in bold 72 px) and gray
  (`#AAAAAA`, subtitle in 38 px, label in semibold 30 px) on black. The recorder's highlight rings and
  click ripples are white, its captions white on black.

## Branding

The logo ships with the skill, in `scripts/assets/`, so it works outside the aspire.dev repository.

- Logo: `scripts/assets/aspire-logo.svg` (from aspire.dev, `src/frontend/src/assets/aspire-logo-256.svg`), rendered
  as `scripts/assets/aspire-logo-1024.png`. The full logo is the symbol followed by the name "Aspire" in white,
  semibold, composed by `Brand.Logo()`; the symbol alone is used on chapter cards. Never recolor the symbol.
- Fonts: the system's, as aspire.dev falls back to them (`system-ui, -apple-system, 'Segoe UI'`, and
  `ui-monospace, Menlo, Consolas` for code). The first one installed wins: Segoe UI, Helvetica Neue, Noto Sans,
  DejaVu Sans, Liberation Sans, then Arial; and for code Cascadia Mono, Consolas, Menlo, SF Mono, Noto Sans Mono,
  DejaVu Sans Mono, Liberation Mono, then Courier New. Nothing is bundled, so the same video can differ slightly in
  type from one operating system to another; produce a series on one machine.
- Colors (from aspire.dev, `src/frontend/src/styles/aspire-brand.css`):

| Name | Hex | Use |
|---|---|---|
| Purple | `#512BD4` | Chapter pills, caption backgrounds in the page; the logo's outline |
| Primary | `#7455DD` | Accents, frame, bars, bullet icons, highlight rings, click ripples |
| Secondary | `#B9AAEE` | Light accents on the dark background: chapter labels on cards |
| Muted | `#DCD5F6` | |
| Magenta | `#D63AFF` | Background glow only, never text |
| Black | `#1F1E33` | Background |
| Dark surface | `#2A2949` / `#3B3A5E` | Cards and panels, borders |
| Dark text | `#F6F3FF` / `#CDC7E8` | Titles / body text and subtitles |
| White | `#FFFFFF` | Logo name, text on purple |

## Recording

- Chromium, headless, through Playwright for .NET, captured with the DevTools screencast (JPEG, quality 92).
- Viewport 1536x864 CSS pixels, device scale factor 1.25, dark color scheme (the dashboard follows it, and it matches
  the frame), `en-US` locale, HTTPS errors ignored for the local development certificate.
- A drawn cursor (white arrow with a dark outline), a `#7455DD` ripple on each click, `#7455DD` highlight rings with
  an optional `#512BD4` caption, all injected in the page by `Scene.cs`.
- The cursor moves with ease-in-out over 25 steps; typing at 55 to 90 ms per character; each step holds until its
  narration ends, plus 0.45 s.
- **Dynamic zooms**: a slight zoom (1.25x) on what the narration points at, so text stays readable without losing
  the page around it. `Scene.cs` records them (`HighlightAsync`, `TypeAsync`, `ZoomAsync`), and the assembly
  renders them on the recorded frames, with sub-pixel precision:
  - 0.8 s, ease-in-out, to zoom in, to zoom out, or to pan from one element to the next.
  - At most 1.4x, and the element fills at most 80% of the width or height: a large element gets a smaller zoom, or
    none under 1.06x. The view never leaves the page: near an edge, the element is off-center.
  - A zoom lasts until the highlights are cleared, the next zoom, a navigation, or the end of the step. A zoom out
    followed within 1.5 s by a zoom in becomes a pan.
  - The cursor, the highlight rings and their captions are in the page, so they zoom with it.
  - `"zoom": false` in the storyboard renders a video without zooms; `scene.AutoZoom = false` keeps one scene wide.
- A throwaway sample app, with realistic but fake data. Never record real credentials, connection strings, tokens,
  subscriptions, or personal data.

## Timing

| Element | Duration |
|---|---|
| Opening and closing cards | 1.0 s + narration + 1.4 s, fading in and out |
| Chapter cards | 2.6 s, no narration |
| Slides | 0.6 s lead on the first, 0.25 s on the others; 0.5 s between slides; 1.0 s tail on the last |
| Recordings | 0.7 s before the first step, 1.0 s after the last narration, 0.35 s fades |

## Voice

- The user picks the narrator. The default is `en-US-AndrewMultilingualNeural`, a neutral, clear voice; the
  shortlist to offer (in `scripts/Speech.cs`; `video.cs voices` makes a sample of each) is:

| Voice | Description |
|---|---|
| `en-US-AndrewMultilingualNeural` | Male, warm and confident (the default) |
| `en-US-BrianMultilingualNeural` | Male, approachable and casual |
| `en-US-ChristopherNeural` | Male, reliable, with authority |
| `en-US-AvaMultilingualNeural` | Female, expressive and friendly |
| `en-US-EmmaMultilingualNeural` | Female, cheerful and clear |
| `en-US-JennyNeural` | Female, friendly and considerate |

  Another voice works only if the free Edge voice service offers it: list them with `video.cs voices --list en-` (or another language prefix). The Azure Voice Gallery has more voices, which the service rejects, such
  as `en-US-PhoebeMultilingualNeural`. Keep one voice across the videos of a series.
- Every voice at `+2%` rate, through the `msedge-tts` npm package (`scripts/tts/tts.mjs`), as a 24 kHz 48 kbps mono mp3.
- Pronunciation fixes in `s_spoken` (`scripts/Speech.cs`), such as "ASP.NET" spoken as "A.S.P. .NET". The
  captions keep the written form.
- Letters said one by one are written as an abbreviation with periods ("A.S.P."), which the voice spells at an even
  pace. A person confirms each new spelling with a sample from `video.cs pronounce` before it's used.
