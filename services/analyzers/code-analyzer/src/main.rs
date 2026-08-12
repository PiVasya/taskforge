mod attestation;
mod security_policy;

use attestation::{PolicyAttestation, POLICY_VERSION};
use axum::{
    extract::{DefaultBodyLimit, State},
    http::StatusCode,
    routing::get,
    routing::post,
    Json, Router,
};
use serde::{Deserialize, Serialize};
use std::{net::SocketAddr, sync::Arc};
use tokio::sync::Semaphore;

#[derive(Clone)]
struct AppState {
    analysis_slots: Arc<Semaphore>,
}

fn taskforge_debug_logs_enabled() -> bool {
    matches!(
        std::env::var("TASKFORGE_DEBUG_LOGS").unwrap_or_default().trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on" | "debug"
    )
}

macro_rules! debug_log {
    ($($arg:tt)*) => {
        if taskforge_debug_logs_enabled() {
            println!($($arg)*);
        }
    };
}

#[derive(Debug, Deserialize)]
struct AnalyzeRequest {
    /// Language key used in TaskForge (e.g. csharp, cpp, c, java, javascript, python, pascal)
    language: String,
    /// Execution profile. `standard` is used for console tasks and `image`
    /// enables only the additional graphics libraries required by image tasks.
    profile: Option<String>,
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
    policy_version: String,
    errors: Vec<Violation>,
    hits: Vec<Hit>,
    #[serde(skip_serializing_if = "Option::is_none")]
    attestation: Option<PolicyAttestation>,
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

fn is_ident_char(c: char) -> bool {
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

/// Match an author-defined task rule without turning every rule into a raw substring.
/// - call-like rules such as `max(` are matched as calls;
/// - plain identifiers/keywords such as `while`, `break`, `list` use token boundaries;
/// - symbolic rules such as `[`, `%`, `sep=` keep whitespace-insensitive matching.
fn find_task_rule_pos(cleaned: &str, rule: &str) -> Option<usize> {
    let trimmed = rule.trim();
    if trimmed.is_empty() {
        return None;
    }

    let compact = strip_ws(trimmed);
    if compact.ends_with('(') {
        return find_call_pos(cleaned, trimmed);
    }

    if compact.chars().all(is_ident_char) {
        return find_identifier_pos(cleaned, &compact);
    }

    find_ws_insensitive_pos(cleaned, trimmed)
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

fn is_c_family(lang: &str) -> bool {
    matches!(lang, "c" | "cpp" | "c++")
}

/// C/C++ translation phase 2 removes a backslash immediately followed by a
/// line break before tokenization. Security checks must inspect the translated
/// token stream, otherwise a continued identifier can hide a forbidden call.
fn splice_c_line_continuations(src: &str) -> String {
    let bytes = src.as_bytes();
    let mut out = String::with_capacity(src.len());
    let mut i = 0usize;
    while i < bytes.len() {
        if bytes[i] == b'\\' {
            if i + 1 < bytes.len() && bytes[i + 1] == b'\n' {
                i += 2;
                continue;
            }
            if i + 1 < bytes.len() && bytes[i + 1] == b'\r' {
                i += if i + 2 < bytes.len() && bytes[i + 2] == b'\n' {
                    3
                } else {
                    2
                };
                continue;
            }
        }
        let ch = src[i..].chars().next().expect("valid utf-8 boundary");
        out.push(ch);
        i += ch.len_utf8();
    }
    out
}

#[derive(Debug)]
struct StaticPolicyHit {
    id: &'static str,
    needle: &'static str,
    message: &'static str,
    position: usize,
}

fn find_identifier_pos(source: &str, name: &str) -> Option<usize> {
    if name.is_empty() {
        return None;
    }
    let mut start = 0usize;
    while let Some(relative) = source[start..].find(name) {
        let pos = start + relative;
        let before = source[..pos].chars().next_back();
        let after_pos = pos + name.len();
        let after = source[after_pos..].chars().next();
        if before.map(is_ident_char).unwrap_or(false)
            || after.map(is_ident_char).unwrap_or(false)
        {
            start = after_pos;
            continue;
        }
        return Some(pos);
    }
    None
}

/// Security-sensitive C/C++ identifiers are rejected as tokens, not only as
/// the literal substring `name(`. This catches macro aliases, function-pointer
/// assignments and other source-level indirection. A linked-object scan in the
/// C++ runner is still the final fail-closed layer.
fn c_family_platform_hits(cleaned: &str) -> Vec<StaticPolicyHit> {
    let mut hits = Vec::new();

    if let Some(position) = cleaned.find("##") {
        hits.push(StaticPolicyHit {
            id: "c.preprocessor_token_paste",
            needle: "##",
            message: "Запрещено использовать склейку токенов препроцессора (##)",
            position,
        });
    }
    if let Some(position) = cleaned.find("%:%:") {
        hits.push(StaticPolicyHit {
            id: "c.preprocessor_token_paste",
            needle: "%:%:",
            message: "Запрещено использовать склейку токенов препроцессора (%:%:)",
            position,
        });
    }

    const RULES: &[(&str, &str, &str)] = &[
        ("c.system", "system", "Запрещено использовать system()"),
        ("c.popen", "popen", "Запрещено использовать popen()"),
        ("c.fork", "fork", "Запрещено создавать процессы через fork()"),
        ("c.vfork", "vfork", "Запрещено создавать процессы через vfork()"),
        ("c.clone", "clone", "Запрещено создавать процессы через clone()"),
        ("c.clone3", "clone3", "Запрещено создавать процессы через clone3()"),
        ("c.execl", "execl", "Запрещено использовать exec*()"),
        ("c.execlp", "execlp", "Запрещено использовать exec*()"),
        ("c.execle", "execle", "Запрещено использовать exec*()"),
        ("c.execv", "execv", "Запрещено использовать exec*()"),
        ("c.execvp", "execvp", "Запрещено использовать exec*()"),
        ("c.execvpe", "execvpe", "Запрещено использовать exec*()"),
        ("c.execve", "execve", "Запрещено использовать exec*()"),
        ("c.execveat", "execveat", "Запрещено использовать exec*()"),
        ("c.posix_spawn", "posix_spawn", "Запрещено создавать внешние процессы"),
        ("c.posix_spawnp", "posix_spawnp", "Запрещено создавать внешние процессы"),
        ("c.dlopen", "dlopen", "Запрещена динамическая загрузка библиотек"),
        ("c.dlmopen", "dlmopen", "Запрещена динамическая загрузка библиотек"),
        ("c.dlsym", "dlsym", "Запрещён динамический поиск системных функций"),
        ("c.dlvsym", "dlvsym", "Запрещён динамический поиск системных функций"),
        ("c.syscall", "syscall", "Запрещены прямые системные вызовы"),
        ("c.prctl", "prctl", "Запрещено изменять политику процесса через prctl()"),
        ("c.seccomp", "seccomp", "Запрещено изменять seccomp-политику процесса"),
        ("c.ptrace", "ptrace", "Запрещено использовать ptrace()"),
        ("c.unshare", "unshare", "Запрещено изменять пространства имён процесса"),
        ("c.setns", "setns", "Запрещено изменять пространства имён процесса"),
        ("c.chroot", "chroot", "Запрещено изменять корневую файловую систему"),
        ("c.pivot_root", "pivot_root", "Запрещено изменять корневую файловую систему"),
        ("c.mprotect", "mprotect", "Запрещено изменять права исполняемой памяти"),
        ("c.memfd_create", "memfd_create", "Запрещено создавать исполняемые файлы в памяти"),
    ];

    for &(id, name, message) in RULES {
        if let Some(position) = find_identifier_pos(cleaned, name) {
            hits.push(StaticPolicyHit {
                id,
                needle: name,
                message,
                position,
            });
        }
    }

    hits
}

#[tokio::main]
async fn main() {
    let debug_logs = taskforge_debug_logs_enabled();
    println!("[code-analyzer] debug logs {}", if debug_logs { "on" } else { "off" });

    // Very verbose boot logs
    debug_log!("[code-analyzer] boot: starting...");
    debug_log!("[code-analyzer] boot: args={:?}", std::env::args().collect::<Vec<_>>());
    debug_log!("[code-analyzer] boot: RUST_LOG={}", std::env::var("RUST_LOG").unwrap_or_else(|_| "<unset>".into()));
    debug_log!("[code-analyzer] boot: RUST_BACKTRACE={}", std::env::var("RUST_BACKTRACE").unwrap_or_else(|_| "<unset>".into()));

    // If RUST_LOG is not set, choose a sane default from TASKFORGE_DEBUG_LOGS.
    let default_filter = if debug_logs { "debug" } else { "info" };
    let filter = tracing_subscriber::EnvFilter::try_from_default_env()
        .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new(default_filter));
    tracing_subscriber::fmt().with_env_filter(filter).init();

    if let Err(error) = security_policy::harden_analyzer_process() {
        eprintln!("[code-analyzer] FATAL: {error}");
        std::process::exit(9);
    }

    if let Err(error) = attestation::readiness_check() {
        eprintln!("[code-analyzer] FATAL: {error}");
        std::process::exit(10);
    }

    let max_parallel = std::env::var("CODE_ANALYZER_MAX_PARALLEL")
        .ok()
        .and_then(|value| value.trim().parse::<usize>().ok())
        .unwrap_or(2)
        .clamp(1, 8);
    let state = AppState {
        analysis_slots: Arc::new(Semaphore::new(max_parallel)),
    };

    let app = Router::new()
        .route("/health", get(|| async { "ok" }))
        .route(
            "/ready",
            get(|| async {
                match attestation::readiness_check() {
                    Ok(()) => (StatusCode::OK, "ok"),
                    Err(_) => (StatusCode::SERVICE_UNAVAILABLE, "not ready"),
                }
            }),
        )
        .route("/analyze", post(analyze))
        .layer(DefaultBodyLimit::max(2 << 20))
        .with_state(state);

    // Port override via PORT env
    let port = std::env::var("PORT").ok().and_then(|v| v.parse::<u16>().ok()).unwrap_or(8080);
    let addr = SocketAddr::from(([0, 0, 0, 0], port));

    tracing::info!("code-analyzer binding on {addr}");
    debug_log!("[code-analyzer] binding on {addr}");

    let listener = tokio::net::TcpListener::bind(addr).await.unwrap_or_else(|e| {
        eprintln!("[code-analyzer] FATAL: failed to bind {addr}: {e}");
        std::process::exit(11);
    });

    tracing::info!("code-analyzer listening on {addr}");
    debug_log!("[code-analyzer] listening OK on {addr}");
    debug_log!("[code-analyzer] ready: GET /health, GET /ready, POST /analyze");

    axum::serve(listener, app).await.unwrap_or_else(|e| {
        eprintln!("[code-analyzer] FATAL: server error: {e}");
        std::process::exit(12);
    });

    // Should never reach here in normal operation
    debug_log!("[code-analyzer] stopped: serve() returned unexpectedly");
}
async fn analyze(
    State(state): State<AppState>,
    Json(req): Json<AnalyzeRequest>,
) -> (StatusCode, Json<AnalyzeResponse>) {
    let _permit = match state.analysis_slots.clone().try_acquire_owned() {
        Ok(permit) => permit,
        Err(_) => {
            return analyzer_rejection(
                StatusCode::TOO_MANY_REQUESTS,
                "analyzer.busy",
                "Сервис анализа кода занят. Повторите запрос позже.",
            );
        }
    };
    tracing::info!("/analyze -> start");
    debug_log!("[code-analyzer] /analyze start lang='{}' source.len={} extra_forbidden={}",
        req.language,
        req.source.len(),
        req.extra_forbidden.as_ref().map(|v| v.len()).unwrap_or(0)
    );
    debug_log!("[code-analyzer] /analyze rules: forbidden_calls={} required_calls={}",
        req.forbidden_calls.as_ref().map(|v| v.len()).unwrap_or(0),
        req.required_calls.as_ref().map(|v| v.len()).unwrap_or(0)
    );

    let Some(lang) = security_policy::normalize_language(&req.language) else {
        return analyzer_rejection(
            StatusCode::BAD_REQUEST,
            "language.unsupported",
            "Неподдерживаемый язык программирования.",
        );
    };
    let Some(profile) = security_policy::normalize_profile(req.profile.as_deref()) else {
        return analyzer_rejection(
            StatusCode::BAD_REQUEST,
            "profile.unsupported",
            "Неподдерживаемый профиль выполнения.",
        );
    };
    if req.source.as_bytes().len() > security_policy::MAX_SOURCE_BYTES {
        return analyzer_rejection(
            StatusCode::PAYLOAD_TOO_LARGE,
            "source.too_large",
            "Исходный код превышает допустимый размер.",
        );
    }
    if req.extra_forbidden.as_ref().map(|v| v.len()).unwrap_or(0) > 64
        || req.forbidden_calls.as_ref().map(|v| v.len()).unwrap_or(0) > 64
        || req.required_calls.as_ref().map(|v| v.len()).unwrap_or(0) > 64
    {
        return analyzer_rejection(
            StatusCode::BAD_REQUEST,
            "rules.too_many",
            "Слишком много пользовательских правил анализа.",
        );
    }
    if req
        .extra_forbidden
        .as_ref()
        .into_iter()
        .flatten()
        .any(|p| p.needle.len() > 256 || p.id.as_deref().unwrap_or("").len() > 128)
        || req
            .forbidden_calls
            .as_ref()
            .into_iter()
            .flatten()
            .chain(req.required_calls.as_ref().into_iter().flatten())
            .any(|v| v.len() > 256)
    {
        return analyzer_rejection(
            StatusCode::BAD_REQUEST,
            "rules.too_large",
            "Пользовательское правило анализа слишком длинное.",
        );
    }

    tracing::debug!("normalized lang={}", lang);

    let analysis_source = if is_c_family(lang) {
        splice_c_line_continuations(&req.source)
    } else {
        req.source.clone()
    };
    let no_comments = strip_comments_only(lang, &analysis_source);
    let cleaned = strip_comments_and_strings(lang, &analysis_source);
    tracing::debug!("cleaned.len={} (orig.len={})", cleaned.len(), req.source.len());
    debug_log!(
        "[code-analyzer] cleaned.len={} orig.len={} (no_comments.len={})",
        cleaned.len(),
        req.source.len(),
        no_comments.len()
    );

    let mut patterns = builtin_forbidden(lang);
    debug_log!("[code-analyzer] builtin patterns={}", patterns.len());
    if let Some(extra) = req.extra_forbidden {
        debug_log!("[code-analyzer] extra patterns={}", extra.len());
        patterns.extend(extra);
    }
    debug_log!("[code-analyzer] total patterns={}", patterns.len());

    let mut hits: Vec<Hit> = Vec::new();
    let mut errors: Vec<Violation> = Vec::new();

    if let Some((pos, ch)) = find_cyrillic_in_code(lang, &analysis_source) {
        hits.push(Hit {
            pattern_id: Some("unicode.cyrillic_in_code".to_string()),
            needle: ch.to_string(),
            position: pos,
            preview: make_preview(&req.source, pos, ch.len_utf8()),
        });
        errors.push(Violation {
            code: "cyrillic_in_code".to_string(),
            message: cyrillic_policy_message().to_string(),
            pattern_id: Some("unicode.cyrillic_in_code".to_string()),
        });
    }

    if is_c_family(lang) {
        for platform_hit in c_family_platform_hits(&cleaned) {
            hits.push(Hit {
                pattern_id: Some(platform_hit.id.to_string()),
                needle: platform_hit.needle.to_string(),
                position: platform_hit.position,
                preview: make_preview(&cleaned, platform_hit.position, platform_hit.needle.len()),
            });
            errors.push(Violation {
                code: "forbidden".to_string(),
                message: platform_hit.message.to_string(),
                pattern_id: Some(platform_hit.id.to_string()),
            });
        }
    }

    for finding in security_policy::analyze_lexical(
        lang,
        profile,
        &req.source,
        &no_comments,
        &cleaned,
    ) {
        let position = finding.position.min(req.source.len());
        hits.push(Hit {
            pattern_id: Some(finding.id.clone()),
            needle: finding.needle.clone(),
            position,
            preview: make_preview(&req.source, position, finding.needle.len().max(1)),
        });
        errors.push(Violation {
            code: "security_policy".to_string(),
            message: finding.message,
            pattern_id: Some(finding.id),
        });
    }

    if is_c_family(lang) && errors.is_empty() {
        let preprocess_language = lang.to_string();
        let preprocess_profile = profile.to_string();
        let preprocess_source = req.source.clone();
        match tokio::task::spawn_blocking(move || {
            security_policy::preprocess_c_family(
                &preprocess_language,
                &preprocess_profile,
                &preprocess_source,
            )
        })
        .await
        {
            Ok(Ok(preprocessed)) => {
                for finding in security_policy::analyze_preprocessed_c_family(profile, &preprocessed) {
                    hits.push(Hit {
                        pattern_id: Some(finding.id.clone()),
                        needle: finding.needle.clone(),
                        position: 0,
                        preview: "Обнаружено после раскрытия препроцессора.".to_string(),
                    });
                    errors.push(Violation {
                        code: "security_policy".to_string(),
                        message: finding.message,
                        pattern_id: Some(finding.id),
                    });
                }
            }
            Ok(Err(message)) => {
                errors.push(Violation {
                    code: "preprocessor_rejected".to_string(),
                    message,
                    pattern_id: Some("c.preprocessor_failed".to_string()),
                });
            }
            Err(_) => {
                return analyzer_rejection(
                    StatusCode::SERVICE_UNAVAILABLE,
                    "analyzer.preprocessor_unavailable",
                    "Сервис безопасной C/C++-проверки временно недоступен.",
                );
            }
        }
    }

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
            debug_log!(
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
    debug_log!("[code-analyzer] call-rules: forbidden_calls={} required_calls={}", forbidden_calls.len(), required_calls.len());
}

for call in &forbidden_calls {
    if call.trim().is_empty() { continue; }

    if let Some(pos) = find_task_rule_pos(&cleaned, call) {
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
    let ok = find_task_rule_pos(&cleaned, call).is_some();
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

    hits.sort_by(|a, b| {
        (a.position, a.pattern_id.as_deref().unwrap_or(""), a.needle.as_str())
            .cmp(&(b.position, b.pattern_id.as_deref().unwrap_or(""), b.needle.as_str()))
    });
    hits.dedup_by(|a, b| {
        a.position == b.position && a.pattern_id == b.pattern_id && a.needle == b.needle
    });
    hits.truncate(256);

    let attestation = if errors.is_empty() {
        match attestation::issue(lang, profile, &req.source) {
            Ok(value) => Some(value),
            Err(error) => {
                tracing::error!("failed to issue code policy attestation: {error}");
                return analyzer_rejection(
                    StatusCode::SERVICE_UNAVAILABLE,
                    "analyzer.attestation_unavailable",
                    "Сервис подписи результатов анализа временно недоступен.",
                );
            }
        }
    } else {
        None
    };

    debug_log!("[code-analyzer] done ok={} errors={} hits={}", errors.is_empty(), errors.len(), hits.len());
    tracing::info!("/analyze <- ok={} errors={} hits={}", errors.is_empty(), errors.len(), hits.len());

    (
        StatusCode::OK,
        Json(AnalyzeResponse {
            ok: errors.is_empty(),
            policy_version: POLICY_VERSION.to_string(),
            errors,
            hits,
            attestation,
        }),
    )
}

fn analyzer_rejection(
    status: StatusCode,
    id: &str,
    message: &str,
) -> (StatusCode, Json<AnalyzeResponse>) {
    (
        status,
        Json(AnalyzeResponse {
            ok: false,
            policy_version: POLICY_VERSION.to_string(),
            errors: vec![Violation {
                code: "analyzer_rejected".to_string(),
                message: message.to_string(),
                pattern_id: Some(id.to_string()),
            }],
            hits: Vec::new(),
            attestation: None,
        }),
    )
}

fn is_cyrillic_char(c: char) -> bool {
    matches!(c as u32,
        0x0400..=0x052F |
        0x1C80..=0x1C8F |
        0x2DE0..=0x2DFF |
        0xA640..=0xA69F
    )
}

fn cyrillic_policy_message() -> &'static str {
    "Кириллица разрешена в строках и комментариях, но запрещена в исполняемом коде: используйте латинские имена переменных, функций и классов."
}

fn find_cyrillic_in_code(lang: &str, src: &str) -> Option<(usize, char)> {
    let has_hash_line_comment = matches!(lang, "python" | "py");
    let has_dash_dash_line_comment = matches!(lang, "sql" | "postgres" | "postgresql");
    let has_pascal_curly_comments = matches!(lang, "pascal");
    let has_pascal_paren_comments = matches!(lang, "pascal");

    let chars: Vec<(usize, char)> = src.char_indices().collect();
    let mut i = 0usize;
    let mut in_line_comment = false;
    let mut in_block_comment = false;
    let mut in_pascal_curly = false;
    let mut in_pascal_paren = false;
    let mut in_string: Option<char> = None;
    let mut in_triple: Option<char> = None;

    while i < chars.len() {
        let (pos, c) = chars[i];
        let next = chars.get(i + 1).map(|(_, ch)| *ch);
        let next2 = chars.get(i + 2).map(|(_, ch)| *ch);

        if in_line_comment {
            if c == '\n' {
                in_line_comment = false;
            }
            i += 1;
            continue;
        }

        if in_block_comment {
            if c == '*' && next == Some('/') {
                in_block_comment = false;
                i += 2;
            } else {
                i += 1;
            }
            continue;
        }

        if in_pascal_curly {
            if c == '}' {
                in_pascal_curly = false;
            }
            i += 1;
            continue;
        }

        if in_pascal_paren {
            if c == '*' && next == Some(')') {
                in_pascal_paren = false;
                i += 2;
            } else {
                i += 1;
            }
            continue;
        }

        if let Some(q) = in_triple {
            if c == q && next == Some(q) && next2 == Some(q) {
                in_triple = None;
                i += 3;
            } else {
                i += 1;
            }
            continue;
        }

        if let Some(q) = in_string {
            // Pascal escapes a quote inside a string by doubling it: 'It''s ok'.
            if lang == "pascal" && c == '\'' && next == Some('\'') {
                i += 2;
                continue;
            }
            if c == '\\' && lang != "pascal" {
                i += if next.is_some() { 2 } else { 1 };
                continue;
            }
            if c == q {
                in_string = None;
            }
            i += 1;
            continue;
        }

        // Start comments before checking chars, so Cyrillic inside comments is allowed.
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
        if has_dash_dash_line_comment && c == '-' && next == Some('-') {
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

        // Start strings before checking chars, so Cyrillic inside literals is allowed.
        if (lang == "python" || lang == "py") && (c == '\'' || c == '"') && next == Some(c) && next2 == Some(c) {
            in_triple = Some(c);
            i += 3;
            continue;
        }
        if c == '\'' || c == '"' || c == '`' {
            in_string = Some(c);
            i += 1;
            continue;
        }

        if is_cyrillic_char(c) {
            return Some((pos, c));
        }

        i += 1;
    }

    None
}

fn make_preview(src: &str, pos: usize, len: usize) -> String {
    let start = nearest_char_boundary_left(src, pos.saturating_sub(30));
    let end = nearest_char_boundary_right(src, (pos + len + 30).min(src.len()));
    let mut s = src[start..end].replace('\n', " ");
    s = s.replace('\r', " ");
    s = s.replace('\t', " ");
    s
}

fn nearest_char_boundary_left(src: &str, mut idx: usize) -> usize {
    idx = idx.min(src.len());
    while idx > 0 && !src.is_char_boundary(idx) { idx -= 1; }
    idx
}

fn nearest_char_boundary_right(src: &str, mut idx: usize) -> usize {
    idx = idx.min(src.len());
    while idx < src.len() && !src.is_char_boundary(idx) { idx += 1; }
    idx
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
    let has_dash_dash_line_comment = matches!(lang, "sql" | "postgres" | "postgresql");
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
        // "--" is not a comment in C/C++/C#/Java/JS/Python/Pascal.
        // Treat it as a comment only for SQL-like dialects, otherwise constructs
        // like x--; system("sh") would hide dangerous code from the analyzer.
        if has_dash_dash_line_comment && c == '-' && next == Some('-') {
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
    let has_dash_dash_line_comment = matches!(lang, "sql" | "postgres" | "postgresql");
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
        if has_dash_dash_line_comment && c == '-' && next == Some('-') {
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_cyrillic_identifier_in_pascal_code() {
        let src = "var число: integer;\nbegin\n  число := 1;\nend.";
        let hit = find_cyrillic_in_code("pascal", src).expect("cyrillic identifier must be detected");
        assert_eq!(hit.1, 'ч');
        assert_eq!(hit.0, src.find('ч').unwrap());
    }

    #[test]
    fn allows_cyrillic_in_pascal_comments_and_strings() {
        let src = "{ русский комментарий }\n(* ещё комментарий *)\nbegin\n  writeln('Привет, мир');\n  writeln('It''s ok');\nend.";
        assert!(find_cyrillic_in_code("pascal", src).is_none());
    }

    #[test]
    fn detects_cyrillic_identifier_in_cpp_code() {
        let src = "// русский комментарий\n#include <iostream>\nint число = 1;\nint main(){ std::cout << \"Привет\"; }";
        let hit = find_cyrillic_in_code("cpp", src).expect("cyrillic identifier must be detected");
        assert_eq!(hit.1, 'ч');
    }

    #[test]
    fn allows_cyrillic_in_python_strings_comments_and_triples() {
        let src = "# русский комментарий\ntext = 'Привет'\nlong_text = \"\"\"Большая строка\"\"\"\nprint(text)";
        assert!(find_cyrillic_in_code("python", src).is_none());
    }

    #[test]
    fn detects_cyrillic_identifier_after_non_ascii_comment_without_index_drift() {
        let src = "// русский комментарий с длинной кириллицей\nint число = 1;";
        let hit = find_cyrillic_in_code("cpp", src).expect("cyrillic identifier must be detected");
        assert_eq!(hit.0, src.find('ч').unwrap());
    }
    #[test]
    fn cpp_decrement_does_not_start_fake_comment_for_forbidden_scan() {
        let src = "int main(){ int x = 1; x--; system(\"sh\"); }";
        let cleaned = strip_comments_and_strings("cpp", src);
        assert!(cleaned.contains("system"));
    }

    #[test]
    fn cpp_decrement_does_not_hide_cyrillic_identifier() {
        let src = "int main(){ int x = 1; x--; int число = 2; }";
        let hit = find_cyrillic_in_code("cpp", src).expect("cyrillic after x-- must be detected");
        assert_eq!(hit.1, 'ч');
    }

    #[test]
    fn cpp_token_paste_macro_is_rejected() {
        let src = "#define RUN(a, b) a##b\nint main(){ RUN(sys, tem)(\"id\"); }";
        let cleaned = strip_comments_and_strings("cpp", src);
        let hits = c_family_platform_hits(&cleaned);
        assert!(hits
            .iter()
            .any(|h| h.id == "c.preprocessor_token_paste"));
    }

    #[test]
    fn cpp_macro_alias_to_system_is_rejected() {
        let src = "#define RUN system\nint main(){ RUN(\"id\"); }";
        let cleaned = strip_comments_and_strings("cpp", src);
        let hits = c_family_platform_hits(&cleaned);
        assert!(hits.iter().any(|h| h.id == "c.system"));
    }

    #[test]
    fn cpp_line_splice_cannot_hide_system_identifier() {
        let src = "int main(){ sys\\\ntem(\"id\"); }";
        let translated = splice_c_line_continuations(src);
        let cleaned = strip_comments_and_strings("cpp", &translated);
        let hits = c_family_platform_hits(&cleaned);
        assert!(hits.iter().any(|h| h.id == "c.system"));
    }

    #[test]
    fn cpp_platform_identifier_uses_token_boundaries() {
        let src = "int filesystem = 1; int ecosystem = 2;";
        let hits = c_family_platform_hits(src);
        assert!(!hits.iter().any(|h| h.id == "c.system"));
    }


    #[test]
    fn task_identifier_rules_use_token_boundaries() {
        assert!(find_task_rule_pos("playlist = 1", "list").is_none());
        assert!(find_task_rule_pos("diff = 1", "if").is_none());
        assert!(find_task_rule_pos("values.append(x)", "append").is_some());
        assert!(find_task_rule_pos("if x > 0:\n    pass", "if").is_some());
    }

    #[test]
    fn task_call_and_symbol_rules_keep_their_intended_shape() {
        assert!(find_task_rule_pos("m = max(values)", "max (").is_some());
        assert!(find_task_rule_pos("print(a, b, sep = '-')", "sep=").is_some());
        assert!(find_task_rule_pos("values = [1, 2]", "[").is_some());
        assert!(find_task_rule_pos("maximum = 1", "max(").is_none());
    }
}

