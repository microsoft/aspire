//! Canvas extensibility.
//!
//! Canvases are sandboxed HTML panels the Deck can host. A canvas is a directory
//! containing a `canvas.json` manifest and an HTML entry point. They are
//! discovered from, in priority order:
//!   1. `$ASPIRE_DECK_CANVASES_DIR` (colon/`;`-separated list of directories)
//!   2. `<user data>/AspireDeck/canvases`
//!   3. the `canvases/` directory shipped next to the app
//!
//! See `.agents/skills/deck-canvas/SKILL.md` for the authoring contract.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

/// The on-disk `canvas.json` manifest.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CanvasManifestFile {
    id: String,
    title: String,
    #[serde(default)]
    description: Option<String>,
    #[serde(default)]
    icon: Option<String>,
    /// Relative path to the HTML entry point. Defaults to "index.html".
    #[serde(default = "default_entry")]
    entry: String,
}

fn default_entry() -> String {
    "index.html".to_string()
}

/// A canvas manifest as sent to the UI, with a resolved URL the webview can load
/// in an `<iframe>`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CanvasManifest {
    pub id: String,
    pub title: String,
    pub description: Option<String>,
    pub icon: Option<String>,
    pub entry: String,
    /// A `canvas://<id>/<entry>` URL served by Deck's custom URI scheme handler.
    /// A raw `file://` URL cannot be used: webviews (notably WKWebView) refuse to
    /// load a `file://` iframe from the app's custom origin, leaving it blank.
    pub url: String,
}

fn manifest_dirs() -> Vec<PathBuf> {
    let mut dirs = Vec::new();

    if let Ok(list) = std::env::var("ASPIRE_DECK_CANVASES_DIR") {
        for part in list.split([':', ';']).filter(|p| !p.is_empty()) {
            dirs.push(PathBuf::from(part));
        }
    }

    if let Some(data_dir) = dirs::data_dir() {
        dirs.push(data_dir.join("AspireDeck").join("canvases"));
    }

    // Directory shipped alongside the executable: <exe dir>/canvases, plus the
    // repo layouts used during local development. With a Cargo build the exe is
    // at `src-tauri/target/<profile>/aspire-deck`, so walking up reaches the
    // `src-tauri/canvases` and the Deck project root `canvases` directories.
    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            dirs.push(exe_dir.join("canvases"));
            dirs.push(exe_dir.join("..").join("..").join("canvases"));
            dirs.push(exe_dir.join("..").join("..").join("..").join("canvases"));
        }
    }

    dirs
}

fn load_manifest(dir: &Path) -> Option<CanvasManifest> {
    let manifest_path = dir.join("canvas.json");
    let contents = std::fs::read_to_string(&manifest_path).ok()?;
    let file: CanvasManifestFile = serde_json::from_str(&contents).ok()?;

    let entry_path = dir.join(&file.entry);
    if !entry_path.exists() {
        return None;
    }

    // Served by the `canvas://` URI scheme handler (see lib.rs). The id is the URL
    // host so relative asset references inside the canvas resolve within the same
    // canvas. The entry is normalized to forward slashes for the URL path.
    let entry_url = file.entry.replace('\\', "/");
    let url = format!("canvas://{}/{}", file.id, entry_url.trim_start_matches('/'));

    Some(CanvasManifest {
        id: file.id,
        title: file.title,
        description: file.description,
        icon: file.icon,
        entry: file.entry,
        url,
    })
}

/// Returns the on-disk directory for the canvas with the given id, if found.
/// Mirrors the discovery order in [`discover`] so the served files match the
/// manifest the UI received.
fn canvas_dir(id: &str) -> Option<PathBuf> {
    for base in manifest_dirs() {
        let entries = match std::fs::read_dir(&base) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if !path.is_dir() {
                continue;
            }
            let manifest_path = path.join("canvas.json");
            let Ok(contents) = std::fs::read_to_string(&manifest_path) else {
                continue;
            };
            let Ok(file) = serde_json::from_str::<CanvasManifestFile>(&contents) else {
                continue;
            };
            if file.id == id {
                return std::fs::canonicalize(&path).ok().or(Some(path));
            }
        }
    }
    None
}

/// Resolves a request for `rel` within the canvas `id` to a readable file on disk.
/// Guards against path traversal: the canonicalized result must stay within the
/// canvas directory. An empty path serves the manifest's entry via `index.html`
/// fallback resolution by the caller.
pub fn resolve_asset(id: &str, rel: &str) -> Option<PathBuf> {
    let root = canvas_dir(id)?;
    let rel = rel.trim_start_matches('/');
    let rel = if rel.is_empty() { "index.html" } else { rel };

    let candidate = root.join(rel);
    let canonical = std::fs::canonicalize(&candidate).ok()?;
    if !canonical.starts_with(&root) {
        return None;
    }
    if canonical.is_file() {
        Some(canonical)
    } else {
        None
    }
}

/// Maps a file extension to a content type for canvas assets.
pub fn content_type_for(path: &Path) -> &'static str {
    match path.extension().and_then(|e| e.to_str()).map(|e| e.to_ascii_lowercase()).as_deref() {
        Some("html" | "htm") => "text/html; charset=utf-8",
        Some("js" | "mjs") => "text/javascript; charset=utf-8",
        Some("css") => "text/css; charset=utf-8",
        Some("json") => "application/json; charset=utf-8",
        Some("svg") => "image/svg+xml",
        Some("png") => "image/png",
        Some("jpg" | "jpeg") => "image/jpeg",
        Some("gif") => "image/gif",
        Some("webp") => "image/webp",
        Some("ico") => "image/x-icon",
        Some("woff2") => "font/woff2",
        Some("woff") => "font/woff",
        Some("wasm") => "application/wasm",
        Some("map") => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    }
}

/// Discovers all available canvases. Later discoveries do not override earlier
/// ones with the same id (first writer wins, matching the directory priority).
pub fn discover() -> Vec<CanvasManifest> {
    let mut found: Vec<CanvasManifest> = Vec::new();

    for base in manifest_dirs() {
        let entries = match std::fs::read_dir(&base) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if !path.is_dir() {
                continue;
            }
            if let Some(manifest) = load_manifest(&path) {
                if !found.iter().any(|m| m.id == manifest.id) {
                    found.push(manifest);
                }
            }
        }
    }

    found
}

// Minimal cross-platform data dir resolution without pulling in the `dirs`
// crate's full surface. We only need the platform user-data directory.
mod dirs {
    use std::path::PathBuf;

    pub fn data_dir() -> Option<PathBuf> {
        #[cfg(target_os = "macos")]
        {
            std::env::var_os("HOME").map(|home| {
                PathBuf::from(home)
                    .join("Library")
                    .join("Application Support")
            })
        }
        #[cfg(target_os = "windows")]
        {
            std::env::var_os("APPDATA").map(PathBuf::from)
        }
        #[cfg(all(unix, not(target_os = "macos")))]
        {
            std::env::var_os("XDG_DATA_HOME")
                .map(PathBuf::from)
                .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local").join("share")))
        }
    }
}
