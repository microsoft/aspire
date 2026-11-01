// Speaks a text with the Microsoft Edge voice, through the msedge-tts npm package, and writes an mp3. video.cs calls
// it; it holds the voice and the rate of every video (see references/specs.md).
//
// Usage: node tts.mjs <voice> <rate> <output.mp3> [--words] < text
//        node tts.mjs --voices
//
// --words prints the words the voice speaks, as a JSON array, for pronunciation samples.
// --voices prints the voices the service offers, as JSON: only these work.
// msedge-tts is installed next to this file, at the exact version of package-lock.json: npm ci --prefix <this folder>.
import { MsEdgeTTS, OUTPUT_FORMAT } from "msedge-tts";
import { writeFileSync } from "node:fs";

if (process.argv[2] === "--voices") {
  const voices = await new MsEdgeTTS().getVoices();
  console.log(JSON.stringify(voices.map((v) => ({
    name: v.ShortName,
    gender: v.Gender,
    personalities: v.VoiceTag?.VoicePersonalities ?? [],
  }))));
  process.exit(0);
}

const [voice, rate, output, ...flags] = process.argv.slice(2);
const words = flags.includes("--words");

// Decodes stdin as UTF-8 across chunks, so a character split between two chunks stays whole.
process.stdin.setEncoding("utf8");
let text = "";
for await (const chunk of process.stdin) {
  text += chunk;
}

// The text goes into an SSML document.
const escaped = text.trim().replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

const tts = new MsEdgeTTS();
await tts.setMetadata(voice, OUTPUT_FORMAT.AUDIO_24KHZ_48KBITRATE_MONO_MP3, { wordBoundaryEnabled: words });
const { audioStream, metadataStream } = tts.toStream(escaped, { rate });

const audio = [];
const spoken = [];
metadataStream?.on("data", (chunk) => {
  for (const item of JSON.parse(chunk.toString()).Metadata ?? []) {
    if (item.Type === "WordBoundary") {
      spoken.push(item.Data?.text?.Text?.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&"));
    }
  }
});

await new Promise((resolve, reject) => {
  audioStream.on("data", (data) => audio.push(data));
  audioStream.once("error", reject);
  audioStream.once("close", resolve);
});
tts.close();

if (audio.length === 0) {
  console.error("No audio received.");
  process.exit(1);
}

writeFileSync(output, Buffer.concat(audio));
if (words) {
  console.log(JSON.stringify(spoken));
}
