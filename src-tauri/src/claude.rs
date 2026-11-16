//! Claude Code durumunu alan yerel HTTP sunucusu.
//!
//! Claude Code hook'ları şu adrese istek atar:
//!   POST http://127.0.0.1:47821/claude/<durum>
//! Durumlar: working | waiting | done | idle
//!
//! Gelen her durum frontend'e `claude-status` olayı olarak iletilir.

use serde::Serialize;
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Emitter};
use tiny_http::{Header, Method, Response, Server};

pub const PORT: u16 = 47821;
const STATES: [&str; 4] = ["working", "waiting", "done", "idle"];

#[derive(Clone, Serialize)]
struct ClaudeStatus {
    state: String,
    at: u64,
}

pub fn start_server(app: AppHandle) {
    thread::spawn(move || {
        let server = match Server::http(("127.0.0.1", PORT)) {
            Ok(s) => s,
            Err(e) => {
                eprintln!("[halisland] Claude sunucusu {PORT} portunda açılamadı: {e}");
                return;
            }
        };

        for request in server.incoming_requests() {
            let path = request.url().trim_end_matches('/').to_string();
            let state = path.strip_prefix("/claude/").unwrap_or("");

            let response = if *request.method() == Method::Post && STATES.contains(&state) {
                let at = SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map(|d| d.as_millis() as u64)
                    .unwrap_or(0);
                let _ = app.emit(
                    "claude-status",
                    ClaudeStatus { state: state.to_string(), at },
                );
                Response::from_string("ok")
            } else if *request.method() == Method::Get && path == "/health" {
                Response::from_string("halisland")
            } else {
                Response::from_string("not found").with_status_code(404)
            };

            let header = Header::from_bytes("Content-Type", "text/plain").unwrap();
            let _ = request.respond(response.with_header(header));
        }
    });
}
