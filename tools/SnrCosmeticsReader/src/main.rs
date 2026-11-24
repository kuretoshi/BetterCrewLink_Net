use base64::Engine;
use serde_json::{json, Value};
use std::{
    io::{self, BufRead, Write},
    path::Path,
};
mod native {
    pub mod mods {
        pub mod snr_cosmetics;
        pub mod sprite;
        pub mod texture;
        pub mod cosmetics {
            pub fn encode(width: u32, height: u32, rgba: &[u8]) -> Result<Vec<u8>, String> {
                let mut out = Vec::new();
                {
                    let mut encoder = png::Encoder::new(&mut out, width, height);
                    encoder.set_color(png::ColorType::Rgba);
                    encoder.set_depth(png::BitDepth::Eight);
                    encoder
                        .write_header()
                        .map_err(|e| e.to_string())?
                        .write_image_data(rgba)
                        .map_err(|e| e.to_string())?;
                }
                Ok(out)
            }
        }
    }
}
fn request(reader: &mut native::mods::snr_cosmetics::Snr, line: &str) -> Result<Value, String> {
    if line.len() > 16384 {
        return Err("Image request exceeds limit".into());
    }
    let value: Value = serde_json::from_str(line).map_err(|e| e.to_string())?;
    let root = Path::new(
        value["root"]
            .as_str()
            .ok_or("Missing selected game directory")?,
    );
    if !root.is_absolute() {
        return Err("Absolute selected game directory required".into());
    }
    let id = value["id"].as_str().ok_or("Missing cosmetic ID")?;
    let part = value["part"].as_str().ok_or("Missing cosmetic part")?;
    reader.update(root);
    let (bytes, adaptive, layout) = reader.image_with_options(id, part)?;
    if bytes.len() > 8 * 1024 * 1024 {
        return Err("Encoded image exceeds limit".into());
    }
    Ok(
        json!({"image":base64::engine::general_purpose::STANDARD.encode(bytes),"adaptive":adaptive,"isSNR":layout}),
    )
}
fn main() {
    let args: Vec<_> = std::env::args().collect();
    let delay = if args.get(1).is_some_and(|arg| arg == "--self-test-delay-ms") {
        args.get(2)
            .and_then(|arg| arg.parse::<u64>().ok())
            .filter(|ms| *ms <= 20000)
            .unwrap_or(0)
    } else {
        0
    };
    let mut reader = native::mods::snr_cosmetics::Snr::default();
    for line in io::stdin().lock().lines() {
        let Ok(line) = line else { break };
        if delay != 0 {
            std::thread::sleep(std::time::Duration::from_millis(delay));
        }
        let result = request(&mut reader, &line).unwrap_or_else(|e| json!({"error":e}));
        if writeln!(io::stdout().lock(), "{result}").is_err() {
            break;
        }
        if io::stdout().flush().is_err() {
            break;
        }
    }
}
