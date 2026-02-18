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
}

#[derive(Debug, Deserialize, Serialize, Clone)]
struct ForbiddenPattern {
    id: Option<String>,
    /// substring match (fast). case_sensitive defaults to true.
    needle: String,
    case_sensitive: Option<bool>,
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
}

#[tokio::main]
async fn main() {
    // Tons of logs by default (MVP), as requested.
    // If RUST_LOG is not set, we default to "debug".
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("debug"));
    tracing_subscriber::fmt().with_env_filter(filter).init();

    let app = Router::new()
        .route("/health", get(|| async { "ok" }))
        .route("/analyze", post(analyze));

    let addr = SocketAddr::from(([0, 0, 0, 0], 8080));
    tracing::info!("code-analyzer listening on {addr}");
    println!("[code-analyzer] listening on {addr}");
    let listener = tokio::net::TcpListener::bind(addr).await.unwrap();
    axum::serve(listener, app).await.unwrap();
}

async fn analyze(Json(req): Json<AnalyzeRequest>) -> Json<AnalyzeResponse> {
    tracing::info!("/analyze -> start");
    println!("[code-analyzer] /analyze start lang='{}' source.len={} extra_forbidden={}",
        req.language,
        req.source.len(),
        req.extra_forbidden.as_ref().map(|v| v.len()).unwrap_or(0)
    );

    let lang = req.language.to_lowercase();
    tracing::debug!("normalized lang={}", lang);

    let cleaned = strip_comments_and_strings(&lang, &req.source);
    tracing::debug!("cleaned.len={} (orig.len={})", cleaned.len(), req.source.len());
    println!("[code-analyzer] cleaned.len={} orig.len={}", cleaned.len(), req.source.len());

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
        let (hay, needle) = if case_sensitive {
            (cleaned.as_str().to_string(), p.needle.clone())
        } else {
            (cleaned.to_lowercase(), p.needle.to_lowercase())
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
            fp("js.child_process", "child_process", "Запрещено использовать child_process"),
            fp("js.require_child_process", "require('child_process'", "Запрещено подключать child_process"),
            fp("js.require_fs", "require('fs'", "Запрещено подключать fs"),
            fp("js.import_fs", "from 'fs'", "Запрещено импортировать fs"),
            fp("js.import_child_process", "from 'child_process'", "Запрещено импортировать child_process"),
        ]);
    }

    if lang == "python" || lang == "py" {
        v.extend(vec![
            fp("py.import_os", "import os", "Запрещено использовать os"),
            fp("py.import_subprocess", "import subprocess", "Запрещено использовать subprocess"),
            fp("py.from_subprocess", "from subprocess", "Запрещено использовать subprocess"),
            fp("py.eval", "eval(", "Запрещено использовать eval()"),
            fp("py.exec", "exec(", "Запрещено использовать exec()"),
            fp("py.open", "open(", "Запрещено читать/писать файлы через open()"),
            fp("py.socket", "import socket", "Запрещено использовать socket"),
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
            fp("c.fopen", "fopen(", "Запрещено использовать fopen()"),
            fp("c.freopen", "freopen(", "Запрещено использовать freopen()"),
            fp("c.open", "open(", "Запрещено использовать open()"),
            fp("c.exec", "exec", "Запрещено использовать exec*()"),
            fp("cpp.std_system", "std::system", "Запрещено использовать std::system"),
        ]);
    }

    if lang == "csharp" || lang == "cs" {
        v.extend(vec![
            fp("cs.process", "System.Diagnostics.Process", "Запрещён запуск процессов"),
            fp("cs.process_start", "Process.Start", "Запрещён запуск процессов"),
            fp("cs.dllimport", "DllImport", "Запрещены P/Invoke (DllImport)"),
            fp("cs.reflection_emit", "Reflection.Emit", "Запрещена генерация кода (Reflection.Emit)"),
            fp("cs.unsafe", "unsafe", "Запрещён unsafe-код"),
            fp("cs.stackalloc", "stackalloc", "Запрещён stackalloc"),
            fp("cs.file", "System.IO", "Запрещены операции с файлами"),
            fp("cs.net", "System.Net", "Запрещена сеть"),
        ]);
    }

    if lang == "java" {
        v.extend(vec![
            fp("java.runtime_exec", "Runtime.getRuntime().exec", "Запрещён запуск процессов (exec)"),
            fp("java.process_builder", "ProcessBuilder", "Запрещён запуск процессов (ProcessBuilder)"),
            fp("java.file", "java.io.", "Запрещены операции с файлами"),
            fp("java.nio", "java.nio.", "Запрещены операции с файлами"),
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
