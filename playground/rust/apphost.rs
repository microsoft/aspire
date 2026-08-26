// Aspire Rust AppHost playground
// Run with: aspire run

#[path = ".aspire/modules/mod.rs"]
mod aspire;

use aspire::*;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let builder = create_builder(None)?;

    let app = builder.add_rust_app("app", "./app")?;
    app.with_http_endpoint(None, None, Some("http"), Some("PORT"), None)?;
    app.with_http_health_check(Some("/health"), None, Some("http"))?;

    let host = builder.build()?;
    host.run(None)?;
    Ok(())
}
