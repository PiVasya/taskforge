use axum::{routing::get, routing::post, Json, Router};
use serde::{Deserialize, Serialize};
use std::net::SocketAddr;

#[derive(Debug, Deserialize)]
struct AnalyzeRequest {
    /// Language key used in TaskForge (e.g. csharp, cpp, c, java, javascript, python, pascal)
    language: String,
    source: String,
    /// Optional extra forbidden patterns configured per task.
    /// Patterns are matched after stripping comments & string literals.
    extra_forbidden: Option<Vec<ForbiddenPattern>>,
    /// Optional per-task forbidden function/method calls. Checked on cleaned source (comments/strings stripped).
    forbidden_calls: Option<Vec<String>>,
    /// Optional per-task required calls. Each must appear at least once as a call.
    required_calls: Option<Vec<String>>,
}


#[derive(Debug, Deserialize, Serialize, Clone)]
struct ForbiddenPattern {
    id: Option<String>,
    /// substring match (fast). case_sensitive defaults to true.
    needle: String,
    case_sensitive: Option<bool>,
    /// If true, pattern is searched in source where comments are stripped but strings are preserved.
    /// Useful for languages where dangerous APIs are typically referenced inside string literals
    /// (e.g. require('child_process') in JS).
    match_in_strings: Option<bool>,
    /// Optional description for UI.
    description: Option<String>,
}

#[derive(Debug, Serialize)]
struct AnalyzeResponse {
    ok: bool,
    errors: Vec<Violation>,
    hits: Vec<Hit>,
}

#[derive(Debug, Serialize)]
struct Violation {
    code: String,
    message: String,
    pattern_id: Option<String>,
}

#[derive(Debug, Serialize)]
struct Hit {
    pattern_id: Option<String>,
    needle: String,
    position: usize,
    preview: String,
}fn is_ident_char(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '_'
}


fn find_call_pos(cleaned: &str, call: &str) -> Option<usize> {
    // Normalize call: remove whitespace, ensure it ends with '(' for "call".
    let mut call_norm: String = call.chars().filter(|c| !c.is_whitespace()).collect();
    if call_norm.is_empty() { return None; }
    if !call_norm.ends_with('(') {
        call_norm.push('(');
    }

    let hay_chars: Vec<char> = cleaned.chars().collect();
    let pat_chars: Vec<char> = call_norm.chars().collect();

    for start in 0..hay_chars.len() {
        if hay_chars[start].is_whitespace() { continue; }

        // boundary: previous character must not be identifier char
        if start > 0 && is_ident_char(hay_chars[start - 1]) {
            continue;
        }

        let mut hi = start;
        let mut pi = 0usize;

        loop {
            while hi < hay_chars.len() && hay_chars[hi].is_whitespace() {
                hi += 1;
            }
            if pi >= pat_chars.len() {
                return Some(start);
            }
            if hi >= hay_chars.len() {
                break;
            }
            if hay_chars[hi] == pat_chars[pi] {
                hi += 1;
                pi += 1;
                continue;
            }
            break;
        }
    }

    None
}

fn strip_ws(s: &str) -> String {
    s.chars().filter(|c| !c.is_whitespace()).collect()
}

/// Find `needle` in `hay` ignoring whitespace. Returns an approximate position in the original `hay`.
fn find_ws_insensitive_pos(hay: &str, needle: &str) -> Option<usize> {
    let n = needle.trim();
    if n.is_empty() { return None; }

    let hay_compact = strip_ws(hay);
    let needle_compact = strip_ws(n);
    if needle_compact.is_empty() { return None; }

    let pos_compact = hay_compact.find(&needle_compact)?;

    // Map compact index back to original index (counting only non-ws chars).
    let mut non_ws = 0usize;
    for (i, ch) in hay.char_indices() {
        if !ch.is_whitespace() {
            if non_ws == pos_compact {
                return Some(i);
            }
            non_ws += 1;
        }
    }
    Some(0)
}

/// Check for a call of a (possibly dotted) name, allowing whitespace around dots and before '('.
/// Example name: "Process.Start" or "__import__" or "solve".
fn has_call(cleaned: &str, name: &str) -> bool {
    if name.trim().is_empty() { return false; }
    let parts: Vec<&str> = name.split('.').map(|p| p.trim()).filter(|p| !p.is_empty()).collect();
    if parts.is_empty() { return false; }

    let hay = cleaned.as_bytes();
    let mut i = 0usize;
    while i < hay.len() {
        // find first part bytes
        if !match_part_at(cleaned, i, parts[0]) {
            i += 1;
            continue;
        }

        // boundary on left
        if i > 0 {
            let prev = cleaned[..i].chars().last().unwrap_or(' ');
            if is_ident_char(prev) { i += 1; continue; }
        }

        let mut pos = i + parts[0].len();
        let mut ok = true;

        // subsequent dotted parts
        for p in parts.iter().skip(1) {
            pos = skip_ws(cleaned, pos);
            if !match_char_at(cleaned, pos, '.') { ok = false; break; }
            pos += 1;
            pos = skip_ws(cleaned, pos);
            if !match_part_at(cleaned, pos, p) { ok = false; break; }
            pos += p.len();
        }

        if ok {
            pos = skip_ws(cleaned, pos);
            if match_char_at(cleaned, pos, '(') {
                return true;
            }
        }

        i += 1;
    }
    false
}

fn skip_ws(s: &str, mut pos: usize) -> usize {
    while pos < s.len() {
        let c = s[pos..].chars().next().unwrap();
        if c.is_whitespace() { pos += c.len_utf8(); } else { break; }
    }
    pos
}

fn match_char_at(s: &str, pos: usize, ch: char) -> bool {
    if pos >= s.len() { return false; }
    s[pos..].chars().next().map(|c| c == ch).unwrap_or(false)
}

fn match_part_at(s: &str, pos: usize, part: &str) -> bool {
    if pos + part.len() > s.len() { return false; }
    s[pos..].starts_with(part)
}



#[tokio::main]
async fn main() {
    // Very verbose boot logs
    println!("[code-analyzer] boot: starting...");
    println!("[code-analyzer] boot: args={:?}", std::env::args().collect::<Vec<_>>());
    println!("[code-analyzer] boot: RUST_LOG={}", std::env::var("RUST_LOG").unwrap_or_else(|_| "<unset>".into()));
    println!("[code-analyzer] boot: RUST_BACKTRACE={}", std::env::var("RUST_BACKTRACE").unwrap_or_else(|_| "<unset>".into()));

    // If RUST_LOG is not set, we default to debug.
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("debug"));
    tracing_subscriber::fmt().with_env_filter(filter).init();

    let app = Router::new()
        .route("/health", get(|| async { "ok" }))
        .route("/analyze", post(analyze));

    // Port override via PORT env
    let port = std::env::var("PORT").ok().and_then(|v| v.parse::<u16>().ok()).unwrap_or(8080);
    let addr = SocketAddr::from(([0, 0, 0, 0], port));

    tracing::info!("code-analyzer binding on {addr}");
    println!("[code-analyzer] binding on {addr}");

    let listener = tokio::net::TcpListener::bind(addr).await.unwrap_or_else(|e| {
        eprintln!("[code-analyzer] FATAL: failed to bind {addr}: {e}");
        std::process::exit(11);
    });

    tracing::info!("code-analyzer listening on {addr}");
    println!("[code-analyzer] listening OK on {addr}");
    println!("[code-analyzer] ready: GET /health, POST /analyze");

    axum::serve(listener, app).await.unwrap_or_else(|e| {
        eprintln!("[code-analyzer] FATAL: server error: {e}");
        std::process::exit(12);
    });

    // Should never reach here in normal operation
    println!("[code-analyzer] stopped: serve() returned unexpectedly");
}
async fn analyze(Json(req): Json<AnalyzeRequest>) -> Json<AnalyzeResponse> {
    tracing::info!("/analyze -> start");
    println!("[code-analyzer] /analyze start lang='{}' source.len={} extra_forbidden={}",
        req.language,
        req.source.len(),
        req.extra_forbidden.as_ref().map(|v| v.len()).unwrap_or(0)
    );
    println!("[code-analyzer] /analyze rules: forbidden_calls={} required_calls={}",
        req.forbidden_calls.as_ref().map(|v| v.len()).unwrap_or(0),
        req.required_calls.as_ref().map(|v| v.len()).unwrap_or(0)
    );

    let lang = req.language.to_lowercase();
    tracing::debug!("normalized lang={}", lang);

    let no_comments = strip_comments_only(&lang, &req.source);
    let cleaned = strip_comments_and_strings(&lang, &req.source);
    tracing::debug!("cleaned.len={} (orig.len={})", cleaned.len(), req.source.len());
    println!(
        "[code-analyzer] cleaned.len={} orig.len={} (no_comments.len={})",
        cleaned.len(),
        req.source.len(),
        no_comments.len()
    );

    let mut patterns = builtin_forbidden(&lang);
    println!("[code-analyzer] builtin patterns={}", patterns.len());
    if let Some(extra) = req.extra_forbidden {
        println!("[code-analyzer] extra patterns={}", extra.len());
        patterns.extend(extra);
    }
    println!("[code-analyzer] total patterns={}", patterns.len());

    let mut hits: Vec<Hit> = Vec::new();
    let mut errors: Vec<Violation> = Vec::new();

    // We scan once per pattern; patterns are small. Later we can optimize with Aho–Corasick.
    for p in patterns {
        tracing::debug!("scan pattern id={:?} needle='{}'", p.id, p.needle);
        let case_sensitive = p.case_sensitive.unwrap_or(true);
        let match_in_strings = p.match_in_strings.unwrap_or(false);

        let base = if match_in_strings { &no_comments } else { &cleaned };
        let (hay, needle) = if case_sensitive {
            (base.as_str().to_string(), p.needle.clone())
        } else {
            (base.to_lowercase(), p.needle.to_lowercase())
        };

        if needle.is_empty() {
            continue;
        }

        // Find all occurrences.
        let mut start = 0usize;
        let mut count = 0usize;
        while let Some(pos) = hay[start..].find(&needle) {
            let abs = start + pos;
            let preview = make_preview(&cleaned, abs, needle.len());
            hits.push(Hit {
                pattern_id: p.id.clone(),
                needle: p.needle.clone(),
                position: abs,
                preview,
            });
            count += 1;
            start = abs + needle.len();
            if start >= hay.len() {
                break;
            }
        }

        if count > 0 {
            println!(
                "[code-analyzer] HIT id={:?} needle='{}' count={} (case_sensitive={})",
                p.id,
                p.needle,
                count,
                case_sensitive
            );
        }

        if hits.iter().any(|h| h.pattern_id == p.id && h.needle == p.needle) {
            errors.push(Violation {
                code: "forbidden".to_string(),
                message: p.description.clone().unwrap_or_else(|| format!("Запрещённая конструкция: {}", p.needle)),
                pattern_id: p.id.clone(),
            });
        }
    }



// ---- Per-task forbidden/required call checks (call = NAME followed by optional spaces and '(' ) ----
// We run these on `cleaned` (comments & strings stripped) to avoid false positives from string literals.
let forbidden_calls = req.forbidden_calls.unwrap_or_default();
let required_calls = req.required_calls.unwrap_or_default();

if !forbidden_calls.is_empty() || !required_calls.is_empty() {
    println!("[code-analyzer] call-rules: forbidden_calls={} required_calls={}", forbidden_calls.len(), required_calls.len());
}

for call in &forbidden_calls {
    if call.trim().is_empty() { continue; }

    // 1) "call-like" check (NAME ... '(' )
    let call_pos = find_call_pos(&cleaned, call);
    // 2) substring check (whitespace-insensitive), useful for tokens like '#include' or 'cout'
    let sub_pos = find_ws_insensitive_pos(&cleaned, call);

    if let Some(pos) = call_pos.or(sub_pos) {
        let needle = call.trim().to_string();
        let preview = make_preview(&cleaned, pos, needle.len().min(32));
        hits.push(Hit {
            pattern_id: Some("task.forbidden_call".to_string()),
            needle: needle.clone(),
            position: pos,
            preview,
        });
        errors.push(Violation {
            code: "forbidden_call".to_string(),
            message: format!("Запрещено: {}", call.trim()),
            pattern_id: Some("task.forbidden_call".to_string()),
        });
    }
}

for call in &required_calls {
    if call.trim().is_empty() { continue; }
    let ok = find_call_pos(&cleaned, call).is_some() || find_ws_insensitive_pos(&cleaned, call).is_some();
    if !ok {
        errors.push(Violation {
            code: "missing_required_call".to_string(),
            message: format!("Не найдено обязательное: {}", call.trim()),
            pattern_id: Some("task.required_call".to_string()),
        });
    }
}
    // Deduplicate errors by (code,pattern_id,message)
    errors.sort_by(|a, b| (a.code.as_str(), a.pattern_id.as_deref().unwrap_or(""), a.message.as_str())
        .cmp(&(b.code.as_str(), b.pattern_id.as_deref().unwrap_or(""), b.message.as_str())));
    errors.dedup_by(|a, b| a.code == b.code && a.pattern_id == b.pattern_id && a.message == b.message);

    println!("[code-analyzer] done ok={} errors={} hits={}", errors.is_empty(), errors.len(), hits.len());
    tracing::info!("/analyze <- ok={} errors={} hits={}", errors.is_empty(), errors.len(), hits.len());

    Json(AnalyzeResponse {
        ok: errors.is_empty(),
        errors,
        hits,
    })
}

fn make_preview(src: &str, pos: usize, len: usize) -> String {
    let start = pos.saturating_sub(30);
    let end = (pos + len + 30).min(src.len());
    let mut s = src[start..end].replace('\n', " ");
    s = s.replace('\r', " ");
    s = s.replace('\t', " ");
    s
}

fn builtin_forbidden(lang: &str) -> Vec<ForbiddenPattern> {
    // NOTE: This is an MVP list of dangerous patterns. It will be expanded later.
    // We strip comments & strings first, so "asm" in a string won't trigger.

    let mut v: Vec<ForbiddenPattern> = Vec::new();

    // Cross-language: eval-like
    if lang == "javascript" || lang == "js" || lang == "typescript" || lang == "ts" {
        v.extend(vec![
            fp("js.eval", "eval(", "Запрещено использовать eval()"),
            fp("js.function_ctor", "Function(", "Запрещено использовать Function-конструктор"),
            // JS: module names are usually inside string literals, so we match with strings preserved.
            fp_str("js.require_child_process_s", "require('child_process", "Запрещено подключать child_process"),
            fp_str("js.require_child_process_d", "require(\"child_process", "Запрещено подключать child_process"),
            fp_str("js.import_child_process_s", "from 'child_process", "Запрещено импортировать child_process"),
            fp_str("js.import_child_process_d", "from \"child_process", "Запрещено импортировать child_process"),
            fp_str("js.child_process", "child_process", "Запрещено использовать child_process"),
        ]);
    }

    if lang == "python" || lang == "py" {
        v.extend(vec![
            fp("py.import_os", "import os", "Запрещено использовать os"),
            fp("py.import_subprocess", "import subprocess", "Запрещено использовать subprocess"),
            fp("py.from_subprocess", "from subprocess", "Запрещено использовать subprocess"),
            // Common bypasses
            fp("py.__import__", "__import__(", "Запрещено использовать __import__()"),
            fp("py.importlib", "import importlib", "Запрещено использовать importlib"),
            fp("py.importlib_module", "importlib.import_module", "Запрещено использовать importlib.import_module"),
            fp("py.eval", "eval(", "Запрещено использовать eval()"),
            fp("py.exec", "exec(", "Запрещено использовать exec()"),
            fp("py.socket", "import socket", "Запрещено использовать socket"),
            // Low-level native / escape hatches
            fp("py.ctypes", "import ctypes", "Запрещено использовать ctypes"),
            fp("py.from_ctypes", "from ctypes", "Запрещено использовать ctypes"),
        ]);
    }

    if lang == "c" || lang == "cpp" || lang == "c++" {
        v.extend(vec![
            fp("c.asm", "asm", "Запрещён inline-assembler (asm)"),
            fp("c.__asm__", "__asm__", "Запрещён inline-assembler (__asm__)"),
            fp("c.__asm", "__asm", "Запрещён inline-assembler (__asm)"),
            fp("c.include_windows", "#include <windows.h>", "Запрещён windows.h"),
            fp("c.include_winsock", "#include <winsock", "Запрещён winsock"),
            fp("c.socket", "socket(", "Запрещены сетевые сокеты"),
            fp("c.connect", "connect(", "Запрещены сетевые соединения"),
            fp("c.system", "system(", "Запрещено использовать system()"),
            fp("c.popen", "popen(", "Запрещено использовать popen()"),
            fp("c.exec", "exec", "Запрещено использовать exec*()"),
            fp("cpp.std_system", "std::system", "Запрещено использовать std::system"),
        ]);
    }

    if lang == "csharp" || lang == "cs" {
        v.extend(vec![
            fp("cs.process", "System.Diagnostics.Process", "Запрещён запуск процессов"),
            fp("cs.using_diagnostics", "using System.Diagnostics", "Запрещён запуск процессов (System.Diagnostics)"),
            fp("cs.new_process", "new Process(", "Запрещён запуск процессов"),
            fp("cs.process_startinfo", "ProcessStartInfo", "Запрещён запуск процессов"),
            fp("cs.process_start", "Process.Start", "Запрещён запуск процессов"),
            fp("cs.dllimport", "DllImport", "Запрещены P/Invoke (DllImport)"),
            fp("cs.reflection_emit", "Reflection.Emit", "Запрещена генерация кода (Reflection.Emit)"),
            fp("cs.type_gettype", "Type.GetType(", "Запрещена рефлексия (Type.GetType)"),
            fp("cs.assembly_load", "Assembly.Load", "Запрещена загрузка сборок (Assembly.Load)"),
            fp("cs.unsafe", "unsafe", "Запрещён unsafe-код"),
            fp("cs.stackalloc", "stackalloc", "Запрещён stackalloc"),
            fp("cs.net", "System.Net", "Запрещена сеть"),
        ]);
    }

    if lang == "java" {
        v.extend(vec![
            fp("java.runtime_exec", "Runtime.getRuntime().exec", "Запрещён запуск процессов (exec)"),
            fp("java.process_builder", "ProcessBuilder", "Запрещён запуск процессов (ProcessBuilder)"),
            fp("java.net", "java.net.", "Запрещена сеть"),
            fp("java.jni", "System.loadLibrary", "Запрещён JNI/Native (loadLibrary)"),
            fp("java.reflection", "java.lang.reflect", "Запрещена рефлексия"),
        ]);
    }

    if lang == "pascal" {
        v.extend(vec![
            fp("pas.asm", "asm", "Запрещён inline-assembler (asm)"),
            fp("pas.exec", "Exec", "Запрещён запуск внешних процессов"),
        ]);
    }

    v
}

fn fp(id: &str, needle: &str, desc: &str) -> ForbiddenPattern {
    ForbiddenPattern {
        id: Some(id.to_string()),
        needle: needle.to_string(),
        case_sensitive: Some(true),
        match_in_strings: Some(false),
        description: Some(desc.to_string()),
    }
}

fn fp_str(id: &str, needle: &str, desc: &str) -> ForbiddenPattern {
    ForbiddenPattern {
        id: Some(id.to_string()),
        needle: needle.to_string(),
        case_sensitive: Some(true),
        match_in_strings: Some(true),
        description: Some(desc.to_string()),
    }
}

/// Strips comments and string literals for a given language.
/// This is a lightweight sanitizer designed for speed. It's not a full lexer.
fn strip_comments_and_strings(lang: &str, src: &str) -> String {
    // Comment styles by language
    let has_hash_line_comment = matches!(lang, "python" | "py");
    let has_pascal_curly_comments = matches!(lang, "pascal");
    let has_pascal_paren_comments = matches!(lang, "pascal");

    let chars: Vec<char> = src.chars().collect();
    let mut out = String::with_capacity(src.len());

    let mut i = 0usize;
    let mut in_line_comment = false;
    let mut in_block_comment = false;
    let mut in_pascal_curly = false;
    let mut in_pascal_paren = false;

    let mut in_string: Option<char> = None; // '"' or '\'' or '`'
    let mut in_triple: Option<char> = None; // python triple quotes ' or "

    while i < chars.len() {
        let c = chars[i];
        let next = if i + 1 < chars.len() { Some(chars[i + 1]) } else { None };
        let next2 = if i + 2 < chars.len() { Some(chars[i + 2]) } else { None };

        // End line comment
        if in_line_comment {
            if c == '\n' {
                in_line_comment = false;
                out.push('\n');
            }
            i += 1;
            continue;
        }

        // End block comment
        if in_block_comment {
            if c == '*' && next == Some('/') {
                in_block_comment = false;
                i += 2;
            } else {
                if c == '\n' { out.push('\n'); }
                i += 1;
            }
            continue;
        }

        if in_pascal_curly {
            if c == '}' {
                in_pascal_curly = false;
            } else if c == '\n' {
                out.push('\n');
            }
            i += 1;
            continue;
        }

        if in_pascal_paren {
            if c == '*' && next == Some(')') {
                in_pascal_paren = false;
                i += 2;
                continue;
            }
            if c == '\n' { out.push('\n'); }
            i += 1;
            continue;
        }

        // End triple string
        if let Some(q) = in_triple {
            if c == q && next == Some(q) && next2 == Some(q) {
                in_triple = None;
                // replace triple quotes with spaces
                out.push(' ');
                out.push(' ');
                out.push(' ');
                i += 3;
            } else {
                if c == '\n' { out.push('\n'); } else { out.push(' '); }
                i += 1;
            }
            continue;
        }

        // End normal string
        if let Some(q) = in_string {
            if c == '\\' {
                // escape: skip next char too
                out.push(' ');
                if let Some(nc) = next {
                    if nc == '\n' { out.push('\n'); } else { out.push(' '); }
                    i += 2;
                } else {
                    i += 1;
                }
                continue;
            }
            if c == q {
                in_string = None;
                out.push(' ');
                i += 1;
                continue;
            }
            if c == '\n' { out.push('\n'); } else { out.push(' '); }
            i += 1;
            continue;
        }

        // Start comments (when not in string)
        if c == '/' && next == Some('/') {
            in_line_comment = true;
            i += 2;
            continue;
        }
        if c == '/' && next == Some('*') {
            in_block_comment = true;
            i += 2;
            continue;
        }
        if has_hash_line_comment && c == '#' {
            in_line_comment = true;
            i += 1;
            continue;
        }
        // Basic support for "--" line comments (some dialects)
        if c == '-' && next == Some('-') {
            in_line_comment = true;
            i += 2;
            continue;
        }
        if has_pascal_curly_comments && c == '{' {
            in_pascal_curly = true;
            i += 1;
            continue;
        }
        if has_pascal_paren_comments && c == '(' && next == Some('*') {
            in_pascal_paren = true;
            i += 2;
            continue;
        }

        // Start strings
        if (lang == "python" || lang == "py") && (c == '\'' || c == '"') && next == Some(c) && next2 == Some(c) {
            in_triple = Some(c);
            out.push(' ');
            out.push(' ');
            out.push(' ');
            i += 3;
            continue;
        }
        if c == '\'' || c == '"' || c == '`' {
            in_string = Some(c);
            out.push(' ');
            i += 1;
            continue;
        }

        // Default
        out.push(c);
        i += 1;
    }

    out
}

/// Strip comments but keep string literals intact.
/// We still track strings so we don't treat comment markers inside strings as comments.
fn strip_comments_only(lang: &str, src: &str) -> String {
    let has_hash_line_comment = matches!(lang, "python" | "py");
    let has_pascal_curly_comments = matches!(lang, "pascal");
    let has_pascal_paren_comments = matches!(lang, "pascal");

    let chars: Vec<char> = src.chars().collect();
    let mut out = String::with_capacity(src.len());

    let mut i = 0usize;
    let mut in_line_comment = false;
    let mut in_block_comment = false;
    let mut in_pascal_curly = false;
    let mut in_pascal_paren = false;
    let mut in_string: Option<char> = None; // '"' or '\'' or '`'
    let mut in_triple: Option<char> = None; // python triple quotes

    while i < chars.len() {
        let c = chars[i];
        let next = if i + 1 < chars.len() { Some(chars[i + 1]) } else { None };
        let next2 = if i + 2 < chars.len() { Some(chars[i + 2]) } else { None };

        if in_line_comment {
            if c == '\n' {
                in_line_comment = false;
                out.push('\n');
            } else {
                out.push(' ');
            }
            i += 1;
            continue;
        }

        if in_block_comment {
            if c == '*' && next == Some('/') {
                in_block_comment = false;
                out.push(' ');
                out.push(' ');
                i += 2;
            } else {
                out.push(if c == '\n' { '\n' } else { ' ' });
                i += 1;
            }
            continue;
        }

        if in_pascal_curly {
            if c == '}' {
                in_pascal_curly = false;
                out.push(' ');
            } else {
                out.push(if c == '\n' { '\n' } else { ' ' });
            }
            i += 1;
            continue;
        }

        if in_pascal_paren {
            if c == '*' && next == Some(')') {
                in_pascal_paren = false;
                out.push(' ');
                out.push(' ');
                i += 2;
            } else {
                out.push(if c == '\n' { '\n' } else { ' ' });
                i += 1;
            }
            continue;
        }

        // Inside triple string (python) - keep as-is until closing
        if let Some(q) = in_triple {
            if c == q && next == Some(q) && next2 == Some(q) {
                in_triple = None;
                out.push(q);
                out.push(q);
                out.push(q);
                i += 3;
            } else {
                out.push(c);
                i += 1;
            }
            continue;
        }

        // Inside normal string - keep as-is
        if let Some(q) = in_string {
            out.push(c);
            if c == '\\' {
                // escape next
                if let Some(nc) = next {
                    out.push(nc);
                    i += 2;
                } else {
                    i += 1;
                }
                continue;
            }
            if c == q {
                in_string = None;
            }
            i += 1;
            continue;
        }

        // Start comments (when not in string)
        if c == '/' && next == Some('/') {
            in_line_comment = true;
            out.push(' ');
            out.push(' ');
            i += 2;
            continue;
        }
        if c == '/' && next == Some('*') {
            in_block_comment = true;
            out.push(' ');
            out.push(' ');
            i += 2;
            continue;
        }
        if has_hash_line_comment && c == '#' {
            in_line_comment = true;
            out.push(' ');
            i += 1;
            continue;
        }
        if c == '-' && next == Some('-') {
            in_line_comment = true;
            out.push(' ');
            out.push(' ');
            i += 2;
            continue;
        }
        if has_pascal_curly_comments && c == '{' {
            in_pascal_curly = true;
            out.push(' ');
            i += 1;
            continue;
        }
        if has_pascal_paren_comments && c == '(' && next == Some('*') {
            in_pascal_paren = true;
            out.push(' ');
            out.push(' ');
            i += 2;
            continue;
        }

        // Start strings
        if (lang == "python" || lang == "py") && (c == '\'' || c == '"') && next == Some(c) && next2 == Some(c) {
            in_triple = Some(c);
            out.push(c);
            out.push(c);
            out.push(c);
            i += 3;
            continue;
        }
        if c == '\'' || c == '"' || c == '`' {
            in_string = Some(c);
            out.push(c);
            i += 1;
            continue;
        }

        out.push(c);
        i += 1;
    }

    out
}
