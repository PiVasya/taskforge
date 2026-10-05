package main

import (
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/pem"
	"errors"
	"fmt"
	"os"
	"regexp"
	"strings"
	"sync"
	"time"
)

const (
	policyAttestationSchema = "taskforge-code-policy-attestation-v2"
	defaultPolicyVersion    = "2026-07-28.4"
	maxAttestationTTL       = 5 * time.Minute
	maxClockSkew            = 30 * time.Second
)

type policyAttestation struct {
	Schema        string `json:"schema"`
	Language      string `json:"language"`
	Profile       string `json:"profile"`
	SourceSHA256  string `json:"sourceSha256"`
	PolicyVersion string `json:"policyVersion"`
	IssuedAtUnix  int64  `json:"issuedAtUnix"`
	ExpiresAtUnix int64  `json:"expiresAtUnix"`
	Nonce         string `json:"nonce"`
	SignatureB64  string `json:"signatureB64"`
}

var (
	policyKeyOnce sync.Once
	policyKey     *rsa.PublicKey
	policyKeyErr  error
	policyNonceRE = regexp.MustCompile(`^[0-9a-f]{32,128}$`)
)

func policyPublicKeyPath() string {
	if value := strings.TrimSpace(os.Getenv("CODE_ANALYZER_PUBLIC_KEY_PATH")); value != "" {
		return value
	}
	return "/run/secrets/code-analyzer-public.pem"
}

func expectedPolicyVersion() string {
	if value := strings.TrimSpace(os.Getenv("CODE_ANALYZER_POLICY_VERSION")); value != "" {
		return value
	}
	return defaultPolicyVersion
}

func loadPolicyPublicKey() (*rsa.PublicKey, error) {
	policyKeyOnce.Do(func() {
		data, err := os.ReadFile(policyPublicKeyPath())
		if err != nil {
			policyKeyErr = fmt.Errorf("read code analyzer public key: %w", err)
			return
		}
		block, _ := pem.Decode(data)
		if block == nil {
			policyKeyErr = errors.New("code analyzer public key is not PEM")
			return
		}
		if parsed, err := x509.ParsePKIXPublicKey(block.Bytes); err == nil {
			if key, ok := parsed.(*rsa.PublicKey); ok {
				if key.N.BitLen() < 3072 {
					policyKeyErr = errors.New("code analyzer RSA key is too small")
					return
				}
				policyKey = key
				return
			}
		}
		if key, err := x509.ParsePKCS1PublicKey(block.Bytes); err == nil {
			if key.N.BitLen() < 3072 {
				policyKeyErr = errors.New("code analyzer RSA key is too small")
				return
			}
			policyKey = key
			return
		}
		policyKeyErr = errors.New("unsupported code analyzer public key format")
	})
	return policyKey, policyKeyErr
}

func policyAttestationReady() error {
	_, err := loadPolicyPublicKey()
	return err
}

func verifyPolicyAttestation(language, profile, source string, att *policyAttestation) error {
	if att == nil {
		return errors.New("missing code analyzer attestation")
	}
	if att.Schema != policyAttestationSchema {
		return errors.New("unsupported code analyzer attestation schema")
	}
	if att.Language != language || att.Profile != profile {
		return errors.New("code analyzer attestation target mismatch")
	}
	if att.PolicyVersion != expectedPolicyVersion() {
		return errors.New("code analyzer policy version mismatch")
	}
	now := time.Now()
	issued := time.Unix(att.IssuedAtUnix, 0)
	expires := time.Unix(att.ExpiresAtUnix, 0)
	if att.IssuedAtUnix <= 0 || att.ExpiresAtUnix <= att.IssuedAtUnix {
		return errors.New("invalid code analyzer attestation lifetime")
	}
	if issued.After(now.Add(maxClockSkew)) {
		return errors.New("code analyzer attestation is from the future")
	}
	if now.After(expires) {
		return errors.New("code analyzer attestation expired")
	}
	if expires.Sub(issued) > maxAttestationTTL {
		return errors.New("code analyzer attestation lifetime is too long")
	}
	if !policyNonceRE.MatchString(att.Nonce) {
		return errors.New("invalid code analyzer attestation nonce")
	}

	sourceDigest := sha256.Sum256([]byte(source))
	actualHash := hex.EncodeToString(sourceDigest[:])
	if !strings.EqualFold(actualHash, att.SourceSHA256) {
		return errors.New("code changed after analyzer approval")
	}

	signature, err := base64.StdEncoding.DecodeString(att.SignatureB64)
	if err != nil || len(signature) == 0 {
		return errors.New("invalid code analyzer signature encoding")
	}
	canonical := fmt.Sprintf(
		"%s\n%s\n%s\n%s\n%s\n%d\n%d\n%s\n",
		policyAttestationSchema,
		att.Language,
		att.Profile,
		strings.ToLower(att.SourceSHA256),
		att.PolicyVersion,
		att.IssuedAtUnix,
		att.ExpiresAtUnix,
		att.Nonce,
	)
	digest := sha256.Sum256([]byte(canonical))
	key, err := loadPolicyPublicKey()
	if err != nil {
		return err
	}
	if err := rsa.VerifyPKCS1v15(key, crypto.SHA256, digest[:], signature); err != nil {
		return errors.New("invalid code analyzer signature")
	}
	return nil
}
