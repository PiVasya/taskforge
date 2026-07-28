use std::fs;
use std::io::{self, BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::process::CommandExt;
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

pub const MAX_SOURCE_BYTES: usize = 1 << 20;
const MAX_PREPROCESSED_BYTES: usize = 8 << 20;
const MAX_PREPROCESSOR_TOTAL_BYTES: usize = 128 << 20;
const MAX_PREPROCESSOR_STDERR_BYTES: u64 = 256 << 10;
const PREPROCESSOR_TIMEOUT: Duration = Duration::from_secs(6);


#[repr(C)]
struct LinuxRLimit {
    rlim_cur: u64,
    rlim_max: u64,
}

const RLIMIT_CPU: i32 = 0;
const RLIMIT_FSIZE: i32 = 1;
const RLIMIT_CORE: i32 = 4;
const RLIMIT_NPROC: i32 = 6;
const RLIMIT_NOFILE: i32 = 7;
const RLIMIT_AS: i32 = 9;
const PR_SET_PDEATHSIG: i32 = 1;
const PR_SET_DUMPABLE: i32 = 4;
const PR_SET_NO_NEW_PRIVS: i32 = 38;
const SIGKILL: i32 = 9;

unsafe extern "C" {
    fn setrlimit(resource: i32, limit: *const LinuxRLimit) -> i32;
    fn prctl(option: i32, ...) -> i32;
}

fn set_preprocessor_limit(resource: i32, value: u64) -> io::Result<()> {
    let limit = LinuxRLimit {
        rlim_cur: value,
        rlim_max: value,
    };
    let result = unsafe { setrlimit(resource, &limit) };
    if result == 0 {
        Ok(())
    } else {
        Err(io::Error::last_os_error())
    }
}

pub fn harden_analyzer_process() -> Result<(), String> {
    set_preprocessor_limit(RLIMIT_CORE, 0)
        .map_err(|e| format!("failed to disable analyzer core dumps: {e}"))?;
    if unsafe { prctl(PR_SET_DUMPABLE, 0, 0, 0, 0) } != 0 {
        return Err(format!("failed to mark analyzer non-dumpable: {}", io::Error::last_os_error()));
    }
    if unsafe { prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) } != 0 {
        return Err(format!("failed to enable analyzer no_new_privs: {}", io::Error::last_os_error()));
    }
    Ok(())
}

fn apply_preprocessor_process_limits() -> io::Result<()> {
    set_preprocessor_limit(RLIMIT_CORE, 0)?;
    set_preprocessor_limit(RLIMIT_CPU, 8)?;
    set_preprocessor_limit(RLIMIT_FSIZE, 16 * 1024 * 1024)?;
    set_preprocessor_limit(RLIMIT_NOFILE, 64)?;
    set_preprocessor_limit(RLIMIT_NPROC, 16)?;
    set_preprocessor_limit(RLIMIT_AS, 512 * 1024 * 1024)?;
    if unsafe { prctl(PR_SET_PDEATHSIG, SIGKILL, 0, 0, 0) } != 0
        || unsafe { prctl(PR_SET_DUMPABLE, 0, 0, 0, 0) } != 0
        || unsafe { prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) } != 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

#[derive(Debug, Clone)]
pub struct PolicyFinding {
    pub id: String,
    pub needle: String,
    pub message: String,
    pub position: usize,
}

impl PolicyFinding {
    fn new(id: &str, needle: &str, message: &str, position: usize) -> Self {
        Self {
            id: id.to_string(),
            needle: needle.to_string(),
            message: message.to_string(),
            position,
        }
    }
}

pub fn normalize_language(value: &str) -> Option<&'static str> {
    match value.trim().to_ascii_lowercase().as_str() {
        "c" | "gcc" => Some("c"),
        "cpp" | "c++" | "cxx" | "g++" => Some("cpp"),
        "csharp" | "c#" | "cs" | "dotnet" => Some("csharp"),
        "java" => Some("java"),
        "javascript" | "js" | "node" | "nodejs" => Some("javascript"),
        "python" | "py" | "python3" => Some("python"),
        "pascal" | "pas" | "pascalabc" | "pascalabc.net" => Some("pascal"),
        _ => None,
    }
}

pub fn normalize_profile(value: Option<&str>) -> Option<&'static str> {
    match value.unwrap_or("standard").trim().to_ascii_lowercase().as_str() {
        "" | "standard" | "judge" | "console" => Some("standard"),
        "image" | "graphics" | "render" => Some("image"),
        _ => None,
    }
}

pub fn analyze_lexical(
    language: &str,
    profile: &str,
    raw: &str,
    no_comments: &str,
    cleaned: &str,
) -> Vec<PolicyFinding> {
    let mut findings = Vec::new();

    if raw.as_bytes().len() > MAX_SOURCE_BYTES {
        findings.push(PolicyFinding::new(
            "source.too_large",
            "source",
            "Исходный код превышает допустимый размер.",
            0,
        ));
        return findings;
    }
    if let Some(position) = raw.as_bytes().iter().position(|b| *b == 0) {
        findings.push(PolicyFinding::new(
            "source.nul_byte",
            "NUL",
            "В исходном коде запрещены NUL-байты.",
            position,
        ));
    }

    match language {
        "c" | "cpp" => analyze_c_family(profile, raw, no_comments, cleaned, &mut findings),
        "python" => analyze_python(profile, no_comments, cleaned, &mut findings),
        "javascript" => analyze_javascript(no_comments, cleaned, &mut findings),
        "java" => analyze_java(cleaned, &mut findings),
        "csharp" => analyze_csharp(cleaned, &mut findings),
        "pascal" => analyze_pascal(profile, raw, cleaned, &mut findings),
        _ => {}
    }

    findings
}

pub fn analyze_preprocessed_c_family(profile: &str, preprocessed: &str) -> Vec<PolicyFinding> {
    let mut findings = Vec::new();
    let rules = c_forbidden_identifiers(profile);
    for (id, token, message) in rules {
        if let Some(position) = find_identifier(preprocessed, token, false) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }
    for (id, token, message) in c_forbidden_facilities() {
        if let Some(position) = find_identifier(preprocessed, token, false) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }
    findings
}

fn analyze_c_family(
    profile: &str,
    raw: &str,
    no_comments: &str,
    cleaned: &str,
    findings: &mut Vec<PolicyFinding>,
) {
    const RAW_RULES: &[(&str, &str, &str)] = &[
        ("c.trigraph", "??", "Триграфы C/C++ запрещены."),
        ("c.token_paste", "##", "Склейка токенов препроцессора запрещена."),
        ("c.token_paste_digraph", "%:%:", "Склейка токенов препроцессора запрещена."),
        ("c.universal_identifier", "\\u", "Unicode-экранирование идентификаторов запрещено."),
        ("c.universal_identifier", "\\U", "Unicode-экранирование идентификаторов запрещено."),
        ("c.inline_asm", "__asm", "Inline assembler запрещён."),
        ("c.compiler_attribute", "__attribute__", "Пользовательские compiler attributes запрещены."),
        ("c.compiler_declspec", "__declspec", "Пользовательские compiler attributes запрещены."),
        ("c.pragma_operator", "_Pragma", "Пользовательские pragmas запрещены."),
        ("c.builtin", "__builtin_", "Низкоуровневые compiler builtins запрещены."),
    ];
    for &(id, needle, message) in RAW_RULES {
        if let Some(position) = raw.find(needle) {
            findings.push(PolicyFinding::new(id, needle, message, position));
        }
    }

    for (line_offset, line) in lines_with_offsets(no_comments) {
        let trimmed = line.trim_start();
        if !trimmed.starts_with('#') && !trimmed.starts_with("%:") {
            continue;
        }
        let directive_text = if let Some(rest) = trimmed.strip_prefix('#') {
            rest.trim_start()
        } else {
            trimmed.trim_start_matches("%:").trim_start()
        };
        let directive = directive_text
            .split(|c: char| c.is_ascii_whitespace())
            .next()
            .unwrap_or("")
            .to_ascii_lowercase();
        match directive.as_str() {
            "include" => validate_c_include(profile, line_offset, directive_text, findings),
            "define" | "undef" | "if" | "ifdef" | "ifndef" | "elif" | "else" | "endif" => {}
            "" => {}
            _ => findings.push(PolicyFinding::new(
                "c.preprocessor_directive",
                &directive,
                "Эта директива препроцессора запрещена в OJ.",
                line_offset,
            )),
        }
    }

    let lower_no_comments = no_comments.to_ascii_lowercase();
    const RESTRICTED_PATHS: &[&str] = &[
        "/proc/", "/sys/", "/dev/", "/etc/", "/run/", "/var/run/", "/root/", "/home/", "../", "..\\",
    ];
    for path in RESTRICTED_PATHS {
        if let Some(position) = lower_no_comments.find(path) {
            findings.push(PolicyFinding::new(
                "c.restricted_path",
                path,
                "Обращение к системным путям запрещено.",
                position,
            ));
        }
    }

    for (id, token, message) in c_forbidden_identifiers(profile) {
        if let Some(position) = find_identifier(cleaned, token, false) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }
    for (id, token, message) in c_forbidden_facilities() {
        if let Some(position) = find_identifier(cleaned, token, false) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }
}

fn validate_c_include(
    profile: &str,
    line_offset: usize,
    directive_text: &str,
    findings: &mut Vec<PolicyFinding>,
) {
    let rest = directive_text
        .strip_prefix("include")
        .unwrap_or(directive_text)
        .trim();
    if rest.starts_with('"') {
        findings.push(PolicyFinding::new(
            "c.local_include",
            "#include \"...\"",
            "Локальные и относительные include-файлы запрещены.",
            line_offset,
        ));
        return;
    }
    if !rest.starts_with('<') {
        findings.push(PolicyFinding::new(
            "c.macro_include",
            "#include",
            "Include через макрос запрещён.",
            line_offset,
        ));
        return;
    }
    let Some(end) = rest.find('>') else {
        findings.push(PolicyFinding::new(
            "c.invalid_include",
            "#include",
            "Некорректный include запрещён.",
            line_offset,
        ));
        return;
    };
    let header = rest[1..end].trim();
    if !allowed_c_header(profile, header) {
        findings.push(PolicyFinding::new(
            "c.header_not_allowed",
            header,
            "Этот заголовочный файл недоступен в безопасном профиле OJ.",
            line_offset,
        ));
    }
}

fn allowed_c_header(profile: &str, header: &str) -> bool {
    const SAFE: &[&str] = &[
        "algorithm", "array", "atomic", "bit", "bitset", "cassert", "cctype", "cerrno",
        "cfenv", "cfloat", "charconv", "chrono", "cinttypes", "climits", "clocale", "cmath",
        "codecvt", "compare", "complex", "concepts", "condition_variable", "csetjmp", "csignal",
        "cstdarg", "cstddef", "cstdint", "cstdio", "cstdlib", "cstring", "ctime", "cuchar",
        "cwchar", "cwctype", "deque", "exception", "execution", "expected", "format",
        "forward_list", "functional", "future", "initializer_list", "iomanip", "ios", "iosfwd",
        "iostream", "iterator", "latch", "limits", "list", "map", "memory", "memory_resource",
        "mutex", "new", "numbers", "numeric", "optional", "ostream", "queue", "random", "ranges",
        "ratio", "regex", "scoped_allocator", "semaphore", "set", "shared_mutex", "source_location",
        "span", "sstream", "stack", "stdexcept", "stop_token", "streambuf", "string", "string_view",
        "syncstream", "system_error", "thread", "tuple", "type_traits", "typeindex", "typeinfo",
        "unordered_map", "unordered_set", "utility", "valarray", "variant", "vector", "version",
        "bits/stdc++.h", "assert.h", "ctype.h", "errno.h", "float.h", "inttypes.h", "limits.h",
        "locale.h", "math.h", "setjmp.h", "signal.h", "stdarg.h", "stdbool.h", "stddef.h",
        "stdint.h", "stdio.h", "stdlib.h", "string.h", "time.h", "uchar.h", "wchar.h", "wctype.h",
        "complex.h", "fenv.h", "iso646.h", "stdalign.h", "stdatomic.h", "tgmath.h", "threads.h",
    ];
    if SAFE.contains(&header) {
        return true;
    }
    profile == "image"
        && matches!(
            header,
            "GL/gl.h" | "GL/glu.h" | "GL/glut.h" | "GL/freeglut.h" | "CTurtle.hpp" | "taskforge_turtle.h"
        )
}

fn c_forbidden_identifiers(_profile: &str) -> &'static [(&'static str, &'static str, &'static str)] {
    &[
        ("c.system", "system", "Запуск команд и оболочки запрещён."),
        ("c.popen", "popen", "Запуск команд и оболочки запрещён."),
        ("c.pclose", "pclose", "Запуск команд и оболочки запрещён."),
        ("c.fork", "fork", "Создание процессов запрещено."),
        ("c.vfork", "vfork", "Создание процессов запрещено."),
        ("c.clone", "clone", "Создание процессов запрещено."),
        ("c.clone3", "clone3", "Создание процессов запрещено."),
        ("c.exec", "execv", "Запуск внешних программ запрещён."),
        ("c.exec", "execve", "Запуск внешних программ запрещён."),
        ("c.exec", "execvp", "Запуск внешних программ запрещён."),
        ("c.exec", "execvpe", "Запуск внешних программ запрещён."),
        ("c.exec", "execl", "Запуск внешних программ запрещён."),
        ("c.exec", "execle", "Запуск внешних программ запрещён."),
        ("c.exec", "execlp", "Запуск внешних программ запрещён."),
        ("c.exec", "execveat", "Запуск внешних программ запрещён."),
        ("c.exec", "fexecve", "Запуск внешних программ запрещён."),
        ("c.spawn", "posix_spawn", "Создание процессов запрещено."),
        ("c.spawn", "posix_spawnp", "Создание процессов запрещено."),
        ("c.wordexp", "wordexp", "Интерпретация shell-выражений запрещена."),
        ("c.dynamic_loading", "dlopen", "Динамическая загрузка библиотек запрещена."),
        ("c.dynamic_loading", "dlmopen", "Динамическая загрузка библиотек запрещена."),
        ("c.dynamic_loading", "dlsym", "Динамический поиск системных функций запрещён."),
        ("c.dynamic_loading", "dlvsym", "Динамический поиск системных функций запрещён."),
        ("c.syscall", "syscall", "Прямые системные вызовы запрещены."),
        ("c.ptrace", "ptrace", "Доступ к другим процессам запрещён."),
        ("c.seccomp", "seccomp", "Изменение sandbox-политики запрещено."),
        ("c.prctl", "prctl", "Изменение sandbox-политики запрещено."),
        ("c.network", "socket", "Сетевые операции запрещены."),
        ("c.network", "socketpair", "Сетевые операции запрещены."),
        ("c.network", "connect", "Сетевые операции запрещены."),
        ("c.network", "bind", "Сетевые операции запрещены."),
        ("c.network", "listen", "Сетевые операции запрещены."),
        ("c.network", "accept", "Сетевые операции запрещены."),
        ("c.network", "accept4", "Сетевые операции запрещены."),
        ("c.network", "sendto", "Сетевые операции запрещены."),
        ("c.network", "recvfrom", "Сетевые операции запрещены."),
        ("c.network", "sendmsg", "Сетевые операции запрещены."),
        ("c.network", "recvmsg", "Сетевые операции запрещены."),
        ("c.network", "sendmmsg", "Сетевые операции запрещены."),
        ("c.network", "recvmmsg", "Сетевые операции запрещены."),
        ("c.network", "shutdown", "Сетевые операции запрещены."),
        ("c.network", "getaddrinfo", "Сетевые операции запрещены."),
        ("c.network", "gethostbyname", "Сетевые операции запрещены."),
        ("c.network", "gethostbyname2", "Сетевые операции запрещены."),
        ("c.environment", "getenv", "Чтение окружения процесса запрещено."),
        ("c.environment", "secure_getenv", "Чтение окружения процесса запрещено."),
        ("c.environment", "setenv", "Изменение окружения процесса запрещено."),
        ("c.environment", "putenv", "Изменение окружения процесса запрещено."),
        ("c.environment", "unsetenv", "Изменение окружения процесса запрещено."),
        ("c.filesystem", "open", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "open64", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "openat", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "openat64", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "creat", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "read", "Низкоуровневое чтение файлов запрещено."),
        ("c.filesystem", "pread", "Низкоуровневое чтение файлов запрещено."),
        ("c.filesystem", "readv", "Низкоуровневое чтение файлов запрещено."),
        ("c.filesystem", "write", "Низкоуровневая запись файлов запрещена."),
        ("c.filesystem", "pwrite", "Низкоуровневая запись файлов запрещена."),
        ("c.filesystem", "writev", "Низкоуровневая запись файлов запрещена."),
        ("c.filesystem", "stat", "Разведка файловой системы запрещена."),
        ("c.filesystem", "lstat", "Разведка файловой системы запрещена."),
        ("c.filesystem", "fstatat", "Разведка файловой системы запрещена."),
        ("c.filesystem", "statx", "Разведка файловой системы запрещена."),
        ("c.filesystem", "access", "Разведка файловой системы запрещена."),
        ("c.filesystem", "faccessat", "Разведка файловой системы запрещена."),
        ("c.filesystem", "getcwd", "Разведка файловой системы запрещена."),
        ("c.filesystem", "chdir", "Изменение рабочего каталога запрещено."),
        ("c.filesystem", "fchdir", "Изменение рабочего каталога запрещено."),
        ("c.filesystem", "unlink", "Изменение файловой системы запрещено."),
        ("c.filesystem", "unlinkat", "Изменение файловой системы запрещено."),
        ("c.filesystem", "rename", "Изменение файловой системы запрещено."),
        ("c.filesystem", "renameat", "Изменение файловой системы запрещено."),
        ("c.filesystem", "renameat2", "Изменение файловой системы запрещено."),
        ("c.filesystem", "mkdir", "Изменение файловой системы запрещено."),
        ("c.filesystem", "mkdirat", "Изменение файловой системы запрещено."),
        ("c.filesystem", "rmdir", "Изменение файловой системы запрещено."),
        ("c.filesystem", "link", "Изменение файловой системы запрещено."),
        ("c.filesystem", "linkat", "Изменение файловой системы запрещено."),
        ("c.filesystem", "symlink", "Изменение файловой системы запрещено."),
        ("c.filesystem", "symlinkat", "Изменение файловой системы запрещено."),
        ("c.filesystem", "chmod", "Изменение прав файлов запрещено."),
        ("c.filesystem", "fchmod", "Изменение прав файлов запрещено."),
        ("c.filesystem", "chown", "Изменение владельца файлов запрещено."),
        ("c.filesystem", "fchown", "Изменение владельца файлов запрещено."),
        ("c.filesystem", "truncate", "Изменение файлов запрещено."),
        ("c.filesystem", "ftruncate", "Изменение файлов запрещено."),
        ("c.filesystem", "fcntl", "Низкоуровневое управление файловыми дескрипторами запрещено."),
        ("c.filesystem", "ioctl", "Низкоуровневое управление устройствами запрещено."),
        ("c.filesystem", "fopen", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "fopen64", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "freopen", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "freopen64", "Произвольный доступ к файлам запрещён."),
        ("c.filesystem", "tmpfile", "Создание произвольных файлов запрещено."),
        ("c.filesystem", "tmpfile64", "Создание произвольных файлов запрещено."),
        ("c.filesystem", "tmpnam", "Создание произвольных файлов запрещено."),
        ("c.filesystem", "opendir", "Обход файловой системы запрещён."),
        ("c.filesystem", "fdopendir", "Обход файловой системы запрещён."),
        ("c.filesystem", "readdir", "Обход файловой системы запрещён."),
        ("c.filesystem", "scandir", "Обход файловой системы запрещён."),
        ("c.filesystem", "ftw", "Обход файловой системы запрещён."),
        ("c.filesystem", "nftw", "Обход файловой системы запрещён."),
        ("c.filesystem", "readlink", "Чтение системных ссылок запрещено."),
        ("c.filesystem", "readlinkat", "Чтение системных ссылок запрещено."),
        ("c.filesystem", "glob", "Обход файловой системы запрещён."),
        ("c.filesystem", "glob64", "Обход файловой системы запрещён."),
        ("c.filesystem", "realpath", "Разведка файловой системы запрещена."),
        ("c.filesystem", "open_by_handle_at", "Низкоуровневый доступ к файлам запрещён."),
        ("c.filesystem", "name_to_handle_at", "Низкоуровневый доступ к файлам запрещён."),
        ("c.namespace", "mount", "Изменение файловых пространств запрещено."),
        ("c.namespace", "umount", "Изменение файловых пространств запрещено."),
        ("c.namespace", "umount2", "Изменение файловых пространств запрещено."),
        ("c.namespace", "chroot", "Изменение файловых пространств запрещено."),
        ("c.namespace", "pivot_root", "Изменение файловых пространств запрещено."),
        ("c.namespace", "unshare", "Изменение namespaces запрещено."),
        ("c.namespace", "setns", "Изменение namespaces запрещено."),
        ("c.kernel", "bpf", "Доступ к опасным интерфейсам ядра запрещён."),
        ("c.kernel", "perf_event_open", "Доступ к опасным интерфейсам ядра запрещён."),
        ("c.kernel", "keyctl", "Доступ к keyring ядра запрещён."),
        ("c.kernel", "add_key", "Доступ к keyring ядра запрещён."),
        ("c.kernel", "request_key", "Доступ к keyring ядра запрещён."),
        ("c.kernel", "userfaultfd", "Опасные механизмы памяти запрещены."),
        ("c.kernel", "io_uring_setup", "io_uring запрещён."),
        ("c.kernel", "io_uring_enter", "io_uring запрещён."),
        ("c.kernel", "io_uring_register", "io_uring запрещён."),
        ("c.memory_exec", "mmap", "Прямое управление отображениями памяти запрещено."),
        ("c.memory_exec", "mmap64", "Прямое управление отображениями памяти запрещено."),
        ("c.memory_exec", "mprotect", "Создание исполняемой памяти запрещено."),
        ("c.memory_exec", "pkey_mprotect", "Создание исполняемой памяти запрещено."),
        ("c.memory_exec", "memfd_create", "Исполняемые файлы в памяти запрещены."),
        ("c.process_access", "process_vm_readv", "Доступ к памяти других процессов запрещён."),
        ("c.process_access", "process_vm_writev", "Доступ к памяти других процессов запрещён."),
        ("c.process_access", "process_madvise", "Доступ к другим процессам запрещён."),
        ("c.process_access", "process_mrelease", "Доступ к другим процессам запрещён."),
        ("c.process_access", "pidfd_open", "Доступ к другим процессам запрещён."),
        ("c.process_access", "pidfd_getfd", "Доступ к другим процессам запрещён."),
        ("c.process_access", "pidfd_send_signal", "Доступ к другим процессам запрещён."),
        ("c.kernel", "fanotify_init", "Опасные интерфейсы ядра запрещены."),
        ("c.kernel", "init_module", "Загрузка модулей ядра запрещена."),
        ("c.kernel", "finit_module", "Загрузка модулей ядра запрещена."),
        ("c.kernel", "delete_module", "Управление модулями ядра запрещено."),
        ("c.kernel", "kexec_load", "Изменение загруженного ядра запрещено."),
        ("c.kernel", "kexec_file_load", "Изменение загруженного ядра запрещено."),
        ("c.kernel", "reboot", "Управление системой запрещено."),
        ("c.kernel", "swapon", "Управление swap запрещено."),
        ("c.kernel", "swapoff", "Управление swap запрещено."),
        ("c.kernel", "acct", "Управление системным учётом запрещено."),
        ("c.kernel", "iopl", "Прямой доступ к устройствам запрещён."),
        ("c.kernel", "ioperm", "Прямой доступ к устройствам запрещён."),
        ("c.inline_asm", "asm", "Inline assembler запрещён."),
        ("c.unsafe_api", "gets", "Небезопасная функция gets() запрещена."),
    ]
}

fn c_forbidden_facilities() -> &'static [(&'static str, &'static str, &'static str)] {
    &[
        ("cpp.filesystem", "filesystem", "std::filesystem недоступен в OJ."),
        ("cpp.file_stream", "fstream", "Файловые потоки недоступны в OJ."),
        ("cpp.file_stream", "ifstream", "Файловые потоки недоступны в OJ."),
        ("cpp.file_stream", "ofstream", "Файловые потоки недоступны в OJ."),
        ("cpp.file_stream", "filebuf", "Файловые потоки недоступны в OJ."),
    ]
}

fn analyze_python(profile: &str, no_comments: &str, cleaned: &str, findings: &mut Vec<PolicyFinding>) {
    let allowed_standard = [
        "math", "cmath", "statistics", "fractions", "decimal", "collections", "itertools", "functools",
        "heapq", "bisect", "array", "re", "string", "random", "typing", "dataclasses", "enum", "copy",
    ];
    let allowed_image = [
        "math", "cmath", "statistics", "fractions", "decimal", "collections", "itertools", "functools",
        "heapq", "bisect", "array", "re", "string", "random", "typing", "dataclasses", "enum", "copy",
        "turtle", "tkinter", "matplotlib", "numpy", "PIL",
    ];
    let allowed = if profile == "image" { &allowed_image[..] } else { &allowed_standard[..] };

    for (offset, line) in lines_with_offsets(cleaned) {
        let trimmed = line.trim_start();
        if let Some(rest) = trimmed.strip_prefix("import ") {
            for module in rest.split(',') {
                let root = module.trim().split_whitespace().next().unwrap_or("").split('.').next().unwrap_or("");
                if !root.is_empty() && !allowed.iter().any(|v| *v == root) {
                    findings.push(PolicyFinding::new(
                        "py.import_not_allowed",
                        root,
                        "Этот Python-модуль недоступен в безопасном профиле OJ.",
                        offset,
                    ));
                }
            }
        } else if let Some(rest) = trimmed.strip_prefix("from ") {
            let module = rest.split_whitespace().next().unwrap_or("");
            let root = module.split('.').next().unwrap_or("");
            if module.starts_with('.') || (!root.is_empty() && !allowed.iter().any(|v| *v == root)) {
                findings.push(PolicyFinding::new(
                    "py.import_not_allowed",
                    module,
                    "Этот Python-модуль недоступен в безопасном профиле OJ.",
                    offset,
                ));
            }
        }
    }

    const TOKENS: &[(&str, &str, &str)] = &[
        ("py.eval", "eval", "Динамическое выполнение кода запрещено."),
        ("py.exec", "exec", "Динамическое выполнение кода запрещено."),
        ("py.compile", "compile", "Динамическая компиляция кода запрещена."),
        ("py.import", "__import__", "Динамический импорт запрещён."),
        ("py.open", "open", "Произвольный доступ к файлам запрещён."),
        ("py.breakpoint", "breakpoint", "Отладчик запрещён."),
        ("py.reflection", "getattr", "Динамическая рефлексия запрещена."),
        ("py.reflection", "setattr", "Динамическая рефлексия запрещена."),
        ("py.reflection", "delattr", "Динамическая рефлексия запрещена."),
        ("py.reflection", "globals", "Доступ к глобальному пространству запрещён."),
        ("py.reflection", "locals", "Доступ к локальному пространству запрещён."),
        ("py.reflection", "vars", "Динамическая рефлексия запрещена."),
        ("py.runtime_helper", "license", "Служебные runtime-объекты запрещены."),
        ("py.runtime_helper", "credits", "Служебные runtime-объекты запрещены."),
        ("py.runtime_helper", "copyright", "Служебные runtime-объекты запрещены."),
        ("py.runtime_helper", "quit", "Служебные runtime-объекты запрещены."),
        ("py.escape", "__subclasses__", "Интроспекция runtime запрещена."),
        ("py.escape", "__globals__", "Интроспекция runtime запрещена."),
        ("py.escape", "__builtins__", "Интроспекция runtime запрещена."),
        ("py.escape", "__code__", "Интроспекция runtime запрещена."),
        ("py.escape", "__closure__", "Интроспекция runtime запрещена."),
        ("py.escape", "__mro__", "Интроспекция runtime запрещена."),
        ("py.escape", "__bases__", "Интроспекция runtime запрещена."),
        ("py.escape", "__loader__", "Интроспекция runtime запрещена."),
        ("py.escape", "__spec__", "Интроспекция runtime запрещена."),
        ("py.escape", "__traceback__", "Интроспекция стеков запрещена."),
        ("py.escape", "tb_frame", "Интроспекция стеков запрещена."),
        ("py.escape", "f_globals", "Интроспекция стеков запрещена."),
        ("py.escape", "f_locals", "Интроспекция стеков запрещена."),
        ("py.escape", "f_builtins", "Интроспекция стеков запрещена."),
        ("py.escape", "gi_frame", "Интроспекция стеков запрещена."),
        ("py.escape", "cr_frame", "Интроспекция стеков запрещена."),
        ("py.escape", "ag_frame", "Интроспекция стеков запрещена."),
    ];
    for &(id, token, message) in TOKENS {
        if let Some(position) = find_identifier(cleaned, token, true) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }

    let lower = no_comments.to_ascii_lowercase();
    for needle in ["/proc/", "/sys/", "/dev/", "/etc/", "/run/", "/root/", "/home/", "../", "subprocess", "ctypes", "importlib", "socket", "marshal", "pickle", "runpy"] {
        if let Some(position) = lower.find(needle) {
            findings.push(PolicyFinding::new(
                "py.restricted_capability",
                needle,
                "Эта системная возможность запрещена в Python OJ.",
                position,
            ));
        }
    }
}

fn analyze_javascript(no_comments: &str, cleaned: &str, findings: &mut Vec<PolicyFinding>) {
    const TOKENS: &[(&str, &str, &str)] = &[
        ("js.eval", "eval", "Динамическое выполнение JavaScript запрещено."),
        ("js.function_ctor", "Function", "Function-конструктор запрещён."),
        ("js.require", "require", "Подключение Node.js-модулей запрещено."),
        ("js.process", "process", "Доступ к процессу Node.js запрещён."),
        ("js.module", "module", "CommonJS module API запрещён."),
        ("js.module", "exports", "CommonJS module API запрещён."),
        ("js.global", "globalThis", "Прямой доступ к глобальному объекту запрещён."),
        ("js.global", "global", "Прямой доступ к глобальному объекту запрещён."),
        ("js.buffer", "Buffer", "Низкоуровневый Node.js Buffer API запрещён."),
        ("js.webassembly", "WebAssembly", "WebAssembly запрещён."),
        ("js.dynamic_import", "import", "Динамический импорт запрещён."),
        ("js.worker", "Worker", "Создание workers запрещено."),
        ("js.shared_memory", "SharedArrayBuffer", "Разделяемая память запрещена."),
        ("js.proxy", "Proxy", "Динамические proxy-объекты запрещены в безопасном профиле."),
    ];
    for &(id, token, message) in TOKENS {
        if let Some(position) = find_identifier(cleaned, token, false) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }
    let lower = no_comments.to_ascii_lowercase();
    for needle in [
        "child_process", "node:child_process", "node:fs", "node:net", "node:http", "node:https",
        "node:dgram", "node:vm", "node:worker_threads", "node:os", "node:module", "node:process", "/proc/", "/sys/", "/etc/", "/run/", "../",
        "fetch(", "websocket", "xmlhttprequest",
    ] {
        if let Some(position) = lower.find(needle) {
            findings.push(PolicyFinding::new(
                "js.restricted_capability",
                needle,
                "Эта системная возможность запрещена в JavaScript OJ.",
                position,
            ));
        }
    }
}

fn analyze_java(cleaned: &str, findings: &mut Vec<PolicyFinding>) {
    const RULES: &[(&str, &str, &str)] = &[
        ("java.process", "ProcessBuilder", "Запуск процессов запрещён."),
        ("java.runtime", "Runtime", "Доступ к Runtime запрещён."),
        ("java.files", "java.io.File", "Произвольный доступ к файлам запрещён."),
        ("java.files", "java.nio.file", "Произвольный доступ к файлам запрещён."),
        ("java.network", "java.net", "Сеть запрещена."),
        ("java.reflection", "java.lang.reflect", "Рефлексия запрещена."),
        ("java.reflection", "MethodHandles", "Низкоуровневая рефлексия запрещена."),
        ("java.unsafe", "Unsafe", "Unsafe API запрещён."),
        ("java.native", "loadLibrary", "Native-библиотеки запрещены."),
        ("java.native", "System.load", "Native-библиотеки запрещены."),
        ("java.classloader", "ClassLoader", "Пользовательские загрузчики классов запрещены."),
        ("java.script", "javax.script", "Динамические скриптовые движки запрещены."),
        ("java.internal", "sun.", "Внутренние API JVM запрещены."),
        ("java.internal", "com.sun.", "Внутренние API JVM запрещены."),
    ];
    for &(id, needle, message) in RULES {
        if let Some(position) = cleaned.find(needle) {
            findings.push(PolicyFinding::new(id, needle, message, position));
        }
    }
}

fn analyze_csharp(cleaned: &str, findings: &mut Vec<PolicyFinding>) {
    const RULES: &[(&str, &str, &str)] = &[
        ("cs.process", "System.Diagnostics", "Запуск и диагностика процессов запрещены."),
        ("cs.process", "ProcessStartInfo", "Запуск процессов запрещён."),
        ("cs.process", "Process.Start", "Запуск процессов запрещён."),
        ("cs.files", "System.IO", "Произвольный доступ к файлам запрещён."),
        ("cs.network", "System.Net", "Сеть запрещена."),
        ("cs.reflection", "System.Reflection", "Рефлексия запрещена."),
        ("cs.reflection", "Type.GetType", "Динамическая рефлексия запрещена."),
        ("cs.reflection", "Activator", "Динамическая активация типов запрещена."),
        ("cs.loader", "Assembly.Load", "Динамическая загрузка сборок запрещена."),
        ("cs.loader", "System.Runtime.Loader", "Динамическая загрузка сборок запрещена."),
        ("cs.native", "DllImport", "P/Invoke запрещён."),
        ("cs.native", "LibraryImport", "P/Invoke запрещён."),
        ("cs.native", "NativeLibrary", "Native-библиотеки запрещены."),
        ("cs.native", "Marshal", "Interop API запрещён."),
        ("cs.environment", "Environment.", "Доступ к окружению и процессу запрещён."),
        ("cs.appdomain", "AppDomain", "AppDomain API запрещён."),
        ("cs.unsafe", "unsafe", "Unsafe-код запрещён."),
        ("cs.unsafe", "stackalloc", "Низкоуровневая работа с памятью запрещена."),
        ("cs.unsafe", "delegate*", "Указатели на функции запрещены."),
        ("cs.dynamic", "dynamic", "Динамическое связывание запрещено в безопасном профиле."),
    ];
    for &(id, needle, message) in RULES {
        if let Some(position) = cleaned.find(needle) {
            findings.push(PolicyFinding::new(id, needle, message, position));
        }
    }
}

fn analyze_pascal(profile: &str, raw: &str, cleaned: &str, findings: &mut Vec<PolicyFinding>) {
    let lower = cleaned.to_ascii_lowercase();
    const TOKENS: &[(&str, &str, &str)] = &[
        ("pas.asm", "asm", "Inline assembler запрещён."),
        ("pas.process", "executeprocess", "Запуск процессов запрещён."),
        ("pas.process", "exec", "Запуск процессов запрещён."),
        ("pas.process", "shell", "Запуск оболочки запрещён."),
        ("pas.files", "fileexists", "Разведка файловой системы запрещена."),
        ("pas.files", "directoryexists", "Разведка файловой системы запрещена."),
        ("pas.files", "findfirst", "Обход файловой системы запрещён."),
        ("pas.files", "findnext", "Обход файловой системы запрещён."),
        ("pas.environment", "getenvironmentvariable", "Чтение окружения запрещено."),
        ("pas.native", "external", "Подключение native-кода запрещено."),
        ("pas.native", "dlopen", "Динамическая загрузка библиотек запрещена."),
        ("pas.network", "tcpclient", "Сеть запрещена."),
        ("pas.network", "httpclient", "Сеть запрещена."),
        ("pas.reflection", "system.reflection", "Рефлексия запрещена."),
    ];
    for &(id, token, message) in TOKENS {
        if let Some(position) = find_identifier(&lower, token, false) {
            findings.push(PolicyFinding::new(id, token, message, position));
        }
    }

    for (position, directive) in pascal_directives(raw) {
        let text = directive.trim().to_ascii_lowercase();
        let command = text
            .split(|c: char| c.is_ascii_whitespace() || c == '+' || c == '-')
            .next()
            .unwrap_or("");
        let safe = matches!(command, "mode" | "modeswitch" | "apptype" | "h" | "r" | "q" | "rangechecks" | "overflowchecks");
        if !safe || text.contains("../") || text.contains('/') || text.contains('\\') {
            findings.push(PolicyFinding::new(
                "pas.directive",
                &directive,
                "Эта директива Pascal запрещена в OJ.",
                position,
            ));
        }
    }

    if profile != "image" && lower.contains("graphabc") {
        findings.push(PolicyFinding::new(
            "pas.graphics_profile",
            "GraphABC",
            "GraphABC разрешён только в image-профиле.",
            lower.find("graphabc").unwrap_or(0),
        ));
    }
}

fn pascal_directives(source: &str) -> Vec<(usize, String)> {
    let bytes = source.as_bytes();
    let mut result = Vec::new();
    let mut index = 0usize;
    while index + 2 < bytes.len() {
        if bytes[index] == b'{' && bytes[index + 1] == b'$' {
            let start = index;
            index += 2;
            let content = index;
            while index < bytes.len() && bytes[index] != b'}' {
                index += 1;
            }
            result.push((start, source[content..index.min(source.len())].to_string()));
        } else if bytes[index] == b'(' && bytes[index + 1] == b'*' && bytes[index + 2] == b'$' {
            let start = index;
            index += 3;
            let content = index;
            while index + 1 < bytes.len() && !(bytes[index] == b'*' && bytes[index + 1] == b')') {
                index += 1;
            }
            result.push((start, source[content..index.min(source.len())].to_string()));
        } else {
            index += 1;
        }
    }
    result
}

pub fn preprocess_c_family(language: &str, profile: &str, source: &str) -> Result<String, String> {
    let nonce = format!("{}-{}", std::process::id(), monotonic_nonce());
    let dir = std::env::temp_dir().join(format!("taskforge-analyzer-{nonce}"));
    fs::create_dir(&dir).map_err(|e| format!("failed to create analyzer temp directory: {e}"))?;
    fs::set_permissions(&dir, fs::Permissions::from_mode(0o700))
        .map_err(|e| format!("failed to secure analyzer temp directory: {e}"))?;
    let extension = if language == "c" { "c" } else { "cpp" };
    let source_path = dir.join(format!("submission.{extension}"));
    let result = (|| {
        fs::write(&source_path, source).map_err(|e| format!("failed to stage source for preprocessing: {e}"))?;
        let compiler = if language == "c" { "/usr/bin/clang" } else { "/usr/bin/clang++" };

        let mut command = Command::new(compiler);
        command
            .arg("-E")
            .arg("-x")
            .arg(if language == "c" { "c" } else { "c++" })
            .arg(if language == "c" { "-std=c17" } else { "-std=c++17" })
            .arg("-fno-color-diagnostics")
            .arg("-fmacro-backtrace-limit=0")
            .arg("-Werror=invalid-pp-token")
            .arg("-Werror=trigraphs");
        if profile == "image" {
            command.arg("-I/opt/taskforge/include");
        }
        command
            .arg(&source_path)
            .current_dir(&dir)
            .env_clear()
            .env("PATH", "/usr/bin:/bin")
            .env("HOME", &dir)
            .env("TMPDIR", &dir)
            .env("LANG", "C")
            .env("LC_ALL", "C")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        unsafe {
            command.pre_exec(apply_preprocessor_process_limits);
        }

        let mut child = command.spawn().map_err(|e| format!("failed to start safe preprocessor: {e}"))?;
        let stdout = child.stdout.take().ok_or_else(|| "preprocessor stdout is unavailable".to_string())?;
        let stderr = child.stderr.take().ok_or_else(|| "preprocessor stderr is unavailable".to_string())?;
        let output_source_path = source_path.clone();
        let stdout_thread = thread::spawn(move || {
            collect_user_preprocessed_reader(stdout, &output_source_path)
        });
        let stderr_thread = thread::spawn(move || {
            let mut data = Vec::new();
            let mut limited = stderr.take(MAX_PREPROCESSOR_STDERR_BYTES);
            let _ = limited.read_to_end(&mut data);
            data
        });

        let deadline = Instant::now() + PREPROCESSOR_TIMEOUT;
        let status = loop {
            if let Some(status) = child.try_wait().map_err(|e| format!("failed to poll preprocessor: {e}"))? {
                break status;
            }
            if Instant::now() >= deadline {
                let _ = child.kill();
                let _ = child.wait();
                return Err("C/C++ preprocessing exceeded the security deadline".to_string());
            }
            thread::sleep(Duration::from_millis(20));
        };
        let preprocessed = stdout_thread
            .join()
            .map_err(|_| "preprocessor output reader failed".to_string())??;
        let _stderr = stderr_thread.join().map_err(|_| "preprocessor error reader failed".to_string())?;
        if !status.success() {
            return Err("C/C++ preprocessing failed; the submission was not attested".to_string());
        }
        Ok(preprocessed)
    })();
    let _ = fs::remove_dir_all(&dir);
    result
}

fn collect_user_preprocessed_reader<R: Read>(reader: R, source_path: &Path) -> Result<String, String> {
    let canonical = source_path.canonicalize().unwrap_or_else(|_| source_path.to_path_buf());
    let mut reader = BufReader::new(reader);
    let mut current_is_user = false;
    let mut collected = String::new();
    let mut total_bytes = 0usize;
    let mut line = Vec::new();

    loop {
        line.clear();
        let read = reader
            .read_until(b'\n', &mut line)
            .map_err(|e| format!("failed to read preprocessor output: {e}"))?;
        if read == 0 {
            break;
        }
        total_bytes = total_bytes.saturating_add(read);
        if total_bytes > MAX_PREPROCESSOR_TOTAL_BYTES {
            return Err("C/C++ preprocessor output exceeded the global security limit".to_string());
        }

        let text = String::from_utf8_lossy(&line);
        if let Some(marker_path) = parse_line_marker(&text) {
            let marker = PathBuf::from(marker_path);
            let resolved = marker.canonicalize().unwrap_or(marker);
            current_is_user = resolved == canonical;
            continue;
        }
        if current_is_user {
            if collected.len().saturating_add(text.len()) > MAX_PREPROCESSED_BYTES {
                return Err("preprocessed user source exceeded the security limit".to_string());
            }
            collected.push_str(&text);
        }
    }
    Ok(collected)
}

fn parse_line_marker(line: &str) -> Option<String> {
    let trimmed = line.trim_start();
    if !trimmed.starts_with('#') {
        return None;
    }
    let first_quote = trimmed.find('"')?;
    let rest = &trimmed[first_quote + 1..];
    let end_quote = rest.find('"')?;
    Some(rest[..end_quote].to_string())
}

fn monotonic_nonce() -> u128 {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|v| v.as_nanos())
        .unwrap_or(0)
}

fn lines_with_offsets(source: &str) -> Vec<(usize, &str)> {
    let mut result = Vec::new();
    let mut offset = 0usize;
    for line in source.split_inclusive('\n') {
        result.push((offset, line));
        offset += line.len();
    }
    if source.is_empty() || source.ends_with('\n') {
        return result;
    }
    result
}

fn is_ident_byte(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || byte == b'_'
}

fn find_identifier(source: &str, token: &str, case_insensitive: bool) -> Option<usize> {
    if token.is_empty() {
        return None;
    }
    let hay_owned;
    let needle_owned;
    let (hay, needle) = if case_insensitive {
        hay_owned = source.to_ascii_lowercase();
        needle_owned = token.to_ascii_lowercase();
        (hay_owned.as_str(), needle_owned.as_str())
    } else {
        (source, token)
    };
    let mut offset = 0usize;
    while let Some(relative) = hay[offset..].find(needle) {
        let position = offset + relative;
        let before = position.checked_sub(1).and_then(|i| hay.as_bytes().get(i)).copied();
        let after = hay.as_bytes().get(position + needle.len()).copied();
        if !before.map(is_ident_byte).unwrap_or(false) && !after.map(is_ident_byte).unwrap_or(false) {
            return Some(position);
        }
        offset = position + needle.len();
        if offset >= hay.len() {
            break;
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_non_literal_include() {
        let mut findings = Vec::new();
        validate_c_include("standard", 0, "include HEADER", &mut findings);
        assert!(findings.iter().any(|f| f.id == "c.macro_include"));
    }

    #[test]
    fn image_profile_allows_glut_header() {
        assert!(allowed_c_header("image", "GL/glut.h"));
        assert!(!allowed_c_header("standard", "GL/glut.h"));
    }

    #[test]
    fn python_rejects_os_import() {
        let mut findings = Vec::new();
        analyze_python("standard", "import os\n", "import os\n", &mut findings);
        assert!(findings.iter().any(|f| f.id == "py.import_not_allowed"));
    }

    #[test]
    fn identifier_boundaries_do_not_match_ecosystem() {
        assert_eq!(find_identifier("int ecosystem = 1;", "system", false), None);
    }

    #[test]
    fn c_family_rejects_token_pasting_and_local_include() {
        let source = "#define JOIN(a,b) a##b\n#include \"local.h\"\nint main(){}";
        let findings = analyze_lexical("cpp", "standard", source, source, source);
        assert!(findings.iter().any(|f| f.id == "c.token_paste"));
        assert!(findings.iter().any(|f| f.id == "c.local_include"));
    }

    #[test]
    fn python_rejects_reflection_and_file_escape() {
        let source = "print(getattr(object, '__subclasses__'))\nopen('/etc/passwd')";
        let findings = analyze_lexical("python", "standard", source, source, source);
        assert!(findings.iter().any(|f| f.id == "py.reflection"));
        assert!(findings.iter().any(|f| f.id == "py.open"));
        assert!(findings.iter().any(|f| f.id == "py.restricted_capability"));
    }

    #[test]
    fn javascript_rejects_dynamic_runtime_access() {
        let source = "const f = Function('return process')";
        let findings = analyze_lexical("javascript", "standard", source, source, source);
        assert!(findings.iter().any(|f| f.id == "js.function_ctor"));
        assert!(findings.iter().any(|f| f.id == "js.process"));
    }

    #[test]
    fn java_rejects_process_builder() {
        let source = "new ProcessBuilder(\"id\").start();";
        let findings = analyze_lexical("java", "standard", source, source, source);
        assert!(findings.iter().any(|f| f.id == "java.process"));
    }

    #[test]
    fn csharp_rejects_reflection_and_native_loading() {
        let source = "System.Reflection.Assembly.Load(data); NativeLibrary.Load(name);";
        let findings = analyze_lexical("csharp", "standard", source, source, source);
        assert!(findings.iter().any(|f| f.id == "cs.reflection"));
        assert!(findings.iter().any(|f| f.id == "cs.native"));
    }

    #[test]
    fn pascal_rejects_external_directive() {
        let source = "function x: integer; external 'libc.so';";
        let findings = analyze_lexical("pascal", "standard", source, source, source);
        assert!(findings.iter().any(|f| f.id == "pas.native"));
    }
}
