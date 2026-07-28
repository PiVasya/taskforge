use serde::Serialize;
use std::fs::{self, File};
use std::io::{Read, Write};
use std::process::{Command, Stdio};
use std::time::{SystemTime, UNIX_EPOCH};
use std::os::unix::fs::PermissionsExt;

pub const ATTESTATION_SCHEMA: &str = "taskforge-code-policy-attestation-v2";
pub const POLICY_VERSION: &str = "2026-07-28.4";
const DEFAULT_TTL_SECONDS: u64 = 180;
const MAX_TTL_SECONDS: u64 = 300;


#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PolicyAttestation {
    pub schema: String,
    pub language: String,
    pub profile: String,
    pub source_sha256: String,
    pub policy_version: String,
    pub issued_at_unix: u64,
    pub expires_at_unix: u64,
    pub nonce: String,
    pub signature_b64: String,
}

impl PolicyAttestation {
    pub fn canonical_message(&self) -> String {
        canonical_message(
            &self.language,
            &self.profile,
            &self.source_sha256,
            &self.policy_version,
            self.issued_at_unix,
            self.expires_at_unix,
            &self.nonce,
        )
    }
}

pub fn readiness_check() -> Result<(), String> {
    let openssl = openssl_path();
    let key = signing_key_path();
    let metadata = fs::symlink_metadata(&key)
        .map_err(|e| format!("code analyzer signing key is unavailable: {e}"))?;
    if metadata.file_type().is_symlink() || !metadata.file_type().is_file() {
        return Err("code analyzer signing key must be a regular non-symlink file".to_string());
    }
    if metadata.len() == 0 || metadata.len() > 64 * 1024 {
        return Err("code analyzer signing key has an invalid size".to_string());
    }
    if metadata.permissions().mode() & 0o077 != 0 {
        return Err("code analyzer signing key permissions must not allow group or other access".to_string());
    }
    let output = Command::new(&openssl)
        .args(["pkey", "-in", &key, "-check", "-text", "-noout"])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .map_err(|e| format!("failed to start openssl: {e}"))?;
    if !output.status.success() {
        return Err("code analyzer signing key is unavailable or invalid".to_string());
    }
    let text = String::from_utf8_lossy(&output.stdout);
    let bits = text
        .lines()
        .find_map(|line| {
            let marker = "Private-Key: (";
            let start = line.find(marker)? + marker.len();
            let end = line[start..].find(" bit")? + start;
            line[start..end].trim().parse::<usize>().ok()
        })
        .ok_or_else(|| "could not determine code analyzer signing key size".to_string())?;
    if bits < 3072 {
        return Err("code analyzer signing key must be at least 3072 bits".to_string());
    }
    Ok(())
}

pub fn issue(language: &str, profile: &str, source: &str) -> Result<PolicyAttestation, String> {
    let source_sha256 = sha256_hex(source.as_bytes())?;
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "system clock is before unix epoch".to_string())?
        .as_secs();
    let ttl = std::env::var("CODE_ANALYZER_ATTESTATION_TTL_SECONDS")
        .ok()
        .and_then(|v| v.trim().parse::<u64>().ok())
        .unwrap_or(DEFAULT_TTL_SECONDS)
        .clamp(30, MAX_TTL_SECONDS);
    let nonce = random_hex(16)?;
    let expires = now.saturating_add(ttl);
    let message = canonical_message(
        language,
        profile,
        &source_sha256,
        POLICY_VERSION,
        now,
        expires,
        &nonce,
    );
    let signature = sign(message.as_bytes())?;

    Ok(PolicyAttestation {
        schema: ATTESTATION_SCHEMA.to_string(),
        language: language.to_string(),
        profile: profile.to_string(),
        source_sha256,
        policy_version: POLICY_VERSION.to_string(),
        issued_at_unix: now,
        expires_at_unix: expires,
        nonce,
        signature_b64: base64_encode(&signature),
    })
}

pub fn canonical_message(
    language: &str,
    profile: &str,
    source_sha256: &str,
    policy_version: &str,
    issued_at_unix: u64,
    expires_at_unix: u64,
    nonce: &str,
) -> String {
    format!(
        "{schema}\n{language}\n{profile}\n{source_sha256}\n{policy_version}\n{issued_at_unix}\n{expires_at_unix}\n{nonce}\n",
        schema = ATTESTATION_SCHEMA,
    )
}

fn openssl_path() -> String {
    std::env::var("CODE_ANALYZER_OPENSSL_PATH")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .unwrap_or_else(|| "/usr/bin/openssl".to_string())
}

fn signing_key_path() -> String {
    std::env::var("CODE_ANALYZER_SIGNING_KEY_PATH")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .unwrap_or_else(|| "/run/secrets/code-analyzer-private.pem".to_string())
}

fn sha256_hex(data: &[u8]) -> Result<String, String> {
    let mut child = Command::new(openssl_path())
        .args(["dgst", "-sha256", "-binary"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("failed to start openssl for hashing: {e}"))?;
    child
        .stdin
        .as_mut()
        .ok_or_else(|| "openssl hashing stdin is unavailable".to_string())?
        .write_all(data)
        .map_err(|e| format!("failed to send source to openssl: {e}"))?;
    drop(child.stdin.take());
    let output = child
        .wait_with_output()
        .map_err(|e| format!("failed to wait for openssl hashing: {e}"))?;
    if !output.status.success() || output.stdout.len() != 32 {
        return Err("openssl failed to hash source".to_string());
    }
    Ok(hex_encode(&output.stdout))
}

fn sign(message: &[u8]) -> Result<Vec<u8>, String> {
    let key = signing_key_path();
    let mut child = Command::new(openssl_path())
        .args(["dgst", "-sha256", "-sign", &key])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| format!("failed to start openssl signer: {e}"))?;
    child
        .stdin
        .as_mut()
        .ok_or_else(|| "openssl signing stdin is unavailable".to_string())?
        .write_all(message)
        .map_err(|e| format!("failed to send attestation to openssl: {e}"))?;
    drop(child.stdin.take());
    let output = child
        .wait_with_output()
        .map_err(|e| format!("failed to wait for openssl signer: {e}"))?;
    if !output.status.success() || output.stdout.is_empty() {
        return Err("openssl failed to sign code policy attestation".to_string());
    }
    Ok(output.stdout)
}

fn random_hex(bytes: usize) -> Result<String, String> {
    let mut data = vec![0u8; bytes];
    File::open("/dev/urandom")
        .and_then(|mut f| f.read_exact(&mut data))
        .map_err(|e| format!("failed to read secure randomness: {e}"))?;
    Ok(hex_encode(&data))
}

fn hex_encode(data: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(data.len() * 2);
    for &byte in data {
        out.push(HEX[(byte >> 4) as usize] as char);
        out.push(HEX[(byte & 0x0f) as usize] as char);
    }
    out
}

fn base64_encode(data: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    let mut i = 0usize;
    while i + 3 <= data.len() {
        let n = ((data[i] as u32) << 16) | ((data[i + 1] as u32) << 8) | data[i + 2] as u32;
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
        out.push(TABLE[((n >> 6) & 63) as usize] as char);
        out.push(TABLE[(n & 63) as usize] as char);
        i += 3;
    }
    match data.len() - i {
        1 => {
            let n = (data[i] as u32) << 16;
            out.push(TABLE[((n >> 18) & 63) as usize] as char);
            out.push(TABLE[((n >> 12) & 63) as usize] as char);
            out.push('=');
            out.push('=');
        }
        2 => {
            let n = ((data[i] as u32) << 16) | ((data[i + 1] as u32) << 8);
            out.push(TABLE[((n >> 18) & 63) as usize] as char);
            out.push(TABLE[((n >> 12) & 63) as usize] as char);
            out.push(TABLE[((n >> 6) & 63) as usize] as char);
            out.push('=');
        }
        _ => {}
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn canonical_message_is_stable() {
        assert_eq!(
            canonical_message("cpp", "standard", "abc", "v1", 1, 2, "n"),
            "taskforge-code-policy-attestation-v2\ncpp\nstandard\nabc\nv1\n1\n2\nn\n"
        );
    }

    #[test]
    fn base64_matches_known_vector() {
        assert_eq!(base64_encode(b"hello"), "aGVsbG8=");
    }
}
