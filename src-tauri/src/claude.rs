//! Claude Code durumunu alan yerel HTTP sunucusu.
//!
//! Önerilen kurulum: tüm hook'lar aynı adrese, hook'un stdin'den gelen
//! JSON'unu gönderir:
//!   curl -s -m 1 --data-binary @- http://127.0.0.1:47821/claude/hook
//! Durum, JSON'daki `hook_event_name` alanından çıkarılır. İstem, çalışan
//! araç, değişen dosyalar ve proje klasörü de bu JSON'dan okunur.
//!
//! Eski kurulum da çalışır (gövdesiz):
//!   POST http://127.0.0.1:47821/claude/<working|waiting|done|idle>
//!
//! Her değişiklikte frontend'e `claude-status` olayı gider.

use crate::terminal::{self, WindowId};
use serde::Serialize;
use serde_json::Value;
use std::collections::HashMap;
use std::io::Read;
use std::sync::Mutex;
use std::thread;
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Emitter, Manager};
use tiny_http::{Header, Method, Request, Response, Server};

pub const PORT: u16 = 47821;
const STATES: [&str; 4] = ["working", "waiting", "done", "idle"];
const MAX_PROMPT: usize = 400;
const MAX_FILES: usize = 50;
/// Dosya değiştiren araçlar.
const EDIT_TOOLS: [&str; 4] = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

#[derive(Clone, Default)]
struct Session {
    state: String,
    cwd: String,
    prompt: String,
    tool: String,
    message: String,
    files: Vec<String>,
    window: Option<WindowId>,
    at: u64,
}

/// Frontend'e giden anlık görüntü (en son hareket eden oturum).
#[derive(Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ClaudeStatus {
    state: String,
    at: u64,
    session_id: String,
    project: String,
    prompt: String,
    tool: String,
    message: String,
    files: Vec<String>,
    can_focus: bool,
    /// Açık oturum sayısı
    sessions: usize,
}

#[derive(Default)]
pub struct ClaudeStore {
    sessions: Mutex<HashMap<String, Session>>,
    current: Mutex<String>,
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

fn project_name(cwd: &str) -> String {
    cwd.trim_end_matches(['/', '\\'])
        .rsplit(['/', '\\'])
        .next()
        .unwrap_or("")
        .to_string()
}

/// Dosya yolunu proje klasörüne göre kısaltır.
fn relative(path: &str, cwd: &str) -> String {
    let norm = |s: &str| s.replace('\\', "/");
    let (p, c) = (norm(path), norm(cwd));
    let c = c.trim_end_matches('/');
    if !c.is_empty() && p.to_lowercase().starts_with(&format!("{}/", c.to_lowercase())) {
        p[c.len() + 1..].to_string()
    } else {
        p
    }
}

fn truncate(s: &str, max: usize) -> String {
    let s = s.trim();
    if s.chars().count() <= max {
        s.to_string()
    } else {
        format!("{}…", s.chars().take(max).collect::<String>().trim_end())
    }
}

fn str_field<'a>(v: &'a Value, key: &str) -> &'a str {
    v.get(key).and_then(Value::as_str).unwrap_or("")
}

/// Bir hook olayını oturum durumuna işler.
fn apply_event(store: &ClaudeStore, path_state: &str, body: &Value, client_port: Option<u16>) {
    let event = str_field(body, "hook_event_name");
    let session_id = match str_field(body, "session_id") {
        "" => "default".to_string(),
        id => id.to_string(),
    };

    let mut sessions = store.sessions.lock().unwrap();

    if event == "SessionEnd" {
        sessions.remove(&session_id);
        let mut current = store.current.lock().unwrap();
        if *current == session_id {
            // En son hareket eden diğer oturuma geç.
            *current = sessions
                .iter()
                .max_by_key(|(_, s)| s.at)
                .map(|(id, _)| id.clone())
                .unwrap_or_default();
        }
        return;
    }

    let session = sessions.entry(session_id.clone()).or_default();
    session.at = now_ms();
    let cwd = str_field(body, "cwd");
    if !cwd.is_empty() {
        session.cwd = cwd.to_string();
    }

    let state = match event {
        "SessionStart" => "idle",
        "UserPromptSubmit" => {
            session.prompt = truncate(str_field(body, "prompt"), MAX_PROMPT);
            session.files.clear();
            session.tool.clear();
            "working"
        }
        "PreToolUse" => {
            session.tool = str_field(body, "tool_name").to_string();
            "working"
        }
        "PostToolUse" => {
            let tool = str_field(body, "tool_name");
            if EDIT_TOOLS.contains(&tool) {
                let input = body.get("tool_input").unwrap_or(&Value::Null);
                let file = match str_field(input, "file_path") {
                    "" => str_field(input, "notebook_path"),
                    f => f,
                };
                if !file.is_empty() {
                    let rel = relative(file, &session.cwd);
                    if !session.files.contains(&rel) && session.files.len() < MAX_FILES {
                        session.files.push(rel);
                    }
                }
            }
            "working"
        }
        "Notification" => {
            session.message = truncate(str_field(body, "message"), 160);
            "waiting"
        }
        "Stop" => {
            session.tool.clear();
            "done"
        }
        // Alt ajan bitti ya da bilinmeyen olay: ana iş sürüyor sayılır.
        "SubagentStop" | "PreCompact" => "working",
        _ => path_state,
    };
    if state != "waiting" {
        session.message.clear();
    }
    if STATES.contains(&state) {
        session.state = state.to_string();
    }

    // Pencereyi istem gönderilince ya da henüz bilinmiyorsa ara.
    if (session.window.is_none() || event == "UserPromptSubmit" || event == "SessionStart")
        && let Some(port) = client_port
        && let Some(w) = terminal::window_for_client_port(port)
    {
        session.window = Some(w);
    }

    *store.current.lock().unwrap() = session_id;
}

fn snapshot(store: &ClaudeStore) -> ClaudeStatus {
    let sessions = store.sessions.lock().unwrap();
    let current = store.current.lock().unwrap();
    let Some(s) = sessions.get(&*current) else {
        return ClaudeStatus {
            state: "idle".into(),
            at: now_ms(),
            ..Default::default()
        };
    };
    ClaudeStatus {
        state: if s.state.is_empty() { "idle".into() } else { s.state.clone() },
        at: s.at,
        session_id: current.clone(),
        project: project_name(&s.cwd),
        prompt: s.prompt.clone(),
        tool: s.tool.clone(),
        message: s.message.clone(),
        files: s.files.clone(),
        can_focus: s.window.is_some(),
        sessions: sessions.len(),
    }
}

/// "Terminale dön": Claude'un çalıştığı pencereyi öne getirir.
#[tauri::command]
pub fn focus_claude(app: AppHandle) -> bool {
    let store = app.state::<ClaudeStore>();
    let window = {
        let sessions = store.sessions.lock().unwrap();
        let current = store.current.lock().unwrap();
        sessions.get(&*current).and_then(|s| s.window)
    };
    window.is_some_and(terminal::focus)
}

/// Frontend açılınca son durumu ister (olayı kaçırmış olabilir).
#[tauri::command]
pub fn claude_status(app: AppHandle) -> ClaudeStatus {
    snapshot(&app.state::<ClaudeStore>())
}

fn handle(app: &AppHandle, mut request: Request) {
    let path = request.url().trim_end_matches('/').to_string();
    let route = path.strip_prefix("/claude/").unwrap_or("");
    let is_post = *request.method() == Method::Post;

    let response = if is_post && (route == "hook" || STATES.contains(&route)) {
        let mut raw = Vec::new();
        let _ = request.as_reader().take(1 << 20).read_to_end(&mut raw);
        // Bazı kabuklar UTF-8 olmayan bayt gönderebilir; yine de okumaya çalış.
        let body: Value = serde_json::from_slice(&raw)
            .or_else(|_| serde_json::from_str(&String::from_utf8_lossy(&raw)))
            .unwrap_or(Value::Null);
        let port = request.remote_addr().map(|a| a.port());

        let store = app.state::<ClaudeStore>();
        let path_state = if route == "hook" { "" } else { route };
        apply_event(&store, path_state, &body, port);
        let _ = app.emit("claude-status", snapshot(&store));
        Response::from_string("ok")
    } else if *request.method() == Method::Get && path == "/health" {
        Response::from_string("halisland")
    } else {
        Response::from_string("not found").with_status_code(404)
    };

    let header = Header::from_bytes("Content-Type", "text/plain").unwrap();
    let _ = request.respond(response.with_header(header));
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
            handle(&app, request);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn relative_paths() {
        assert_eq!(relative("C:\\proj\\src\\a.ts", "C:\\proj"), "src/a.ts");
        assert_eq!(relative("/home/x/p/a.rs", "/home/x/p/"), "a.rs");
        assert_eq!(relative("/other/a.rs", "/home/x/p"), "/other/a.rs");
        assert_eq!(project_name("C:\\Users\\PC\\Halisland\\"), "Halisland");
    }

    #[test]
    fn hook_flow() {
        let store = ClaudeStore::default();
        let base = |event: &str| json!({ "session_id": "s1", "cwd": "/p/app", "hook_event_name": event });

        let mut prompt = base("UserPromptSubmit");
        prompt["prompt"] = json!("faz 2'yi yap");
        apply_event(&store, "", &prompt, None);
        let s = snapshot(&store);
        assert_eq!((s.state.as_str(), s.prompt.as_str(), s.project.as_str()), ("working", "faz 2'yi yap", "app"));

        let mut edit = base("PostToolUse");
        edit["tool_name"] = json!("Edit");
        edit["tool_input"] = json!({ "file_path": "/p/app/src/main.ts" });
        apply_event(&store, "", &edit, None);
        apply_event(&store, "", &edit, None);
        assert_eq!(snapshot(&store).files, vec!["src/main.ts"]);

        let mut note = base("Notification");
        note["message"] = json!("Claude needs your permission to use Bash");
        apply_event(&store, "", &note, None);
        assert_eq!(snapshot(&store).state, "waiting");

        apply_event(&store, "", &base("Stop"), None);
        let s = snapshot(&store);
        assert_eq!((s.state.as_str(), s.message.as_str()), ("done", ""));

        apply_event(&store, "", &base("SessionEnd"), None);
        assert_eq!(snapshot(&store).state, "idle");
    }

    #[test]
    fn legacy_routes() {
        let store = ClaudeStore::default();
        apply_event(&store, "working", &Value::Null, None);
        assert_eq!(snapshot(&store).state, "working");
        apply_event(&store, "done", &Value::Null, None);
        assert_eq!(snapshot(&store).state, "done");
    }
}
