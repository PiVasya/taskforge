package main

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

func makePolicyAttestationForTest(t *testing.T, key *rsa.PrivateKey, language, profile, source string, issued, expires int64) *policyAttestation {
	t.Helper()
	sourceDigest := sha256.Sum256([]byte(source))
	sourceHash := hex.EncodeToString(sourceDigest[:])
	nonce := "00112233445566778899aabbccddeeff"
	canonical := fmt.Sprintf(
		"%s\n%s\n%s\n%s\n%s\n%d\n%d\n%s\n",
		policyAttestationSchema,
		language,
		profile,
		sourceHash,
		defaultPolicyVersion,
		issued,
		expires,
		nonce,
	)
	digest := sha256.Sum256([]byte(canonical))
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest[:])
	if err != nil {
		t.Fatal(err)
	}
	return &policyAttestation{
		Schema:        policyAttestationSchema,
		Language:      language,
		Profile:       profile,
		SourceSHA256:  sourceHash,
		PolicyVersion: defaultPolicyVersion,
		IssuedAtUnix:  issued,
		ExpiresAtUnix: expires,
		Nonce:         nonce,
		SignatureB64:  base64.StdEncoding.EncodeToString(signature),
	}
}

func installPolicyTestKey(t *testing.T) *rsa.PrivateKey {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 3072)
	if err != nil {
		t.Fatal(err)
	}
	publicDER, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "code-analyzer-public.pem")
	if err := os.WriteFile(path, pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: publicDER}), 0o600); err != nil {
		t.Fatal(err)
	}
	t.Setenv("CODE_ANALYZER_PUBLIC_KEY_PATH", path)
	t.Setenv("CODE_ANALYZER_POLICY_VERSION", defaultPolicyVersion)
	policyKeyOnce = sync.Once{}
	policyKey = nil
	policyKeyErr = nil
	t.Cleanup(func() {
		policyKeyOnce = sync.Once{}
		policyKey = nil
		policyKeyErr = nil
	})
	return key
}

func TestPolicyAttestationAcceptsExactApprovedSource(t *testing.T) {
	key := installPolicyTestKey(t)
	now := time.Now().Unix()
	source := "int main(){return 0;}"
	attestation := makePolicyAttestationForTest(t, key, "cpp", "standard", source, now-1, now+60)
	if err := verifyPolicyAttestation("cpp", "standard", source, attestation); err != nil {
		t.Fatalf("valid attestation rejected: %v", err)
	}
}

func TestPolicyAttestationRejectsChangedSource(t *testing.T) {
	key := installPolicyTestKey(t)
	now := time.Now().Unix()
	attestation := makePolicyAttestationForTest(t, key, "cpp", "standard", "int main(){return 0;}", now-1, now+60)
	if err := verifyPolicyAttestation("cpp", "standard", "int main(){return 1;}", attestation); err == nil {
		t.Fatal("attestation accepted a changed source")
	}
}

func TestPolicyAttestationRejectsExpiredApproval(t *testing.T) {
	key := installPolicyTestKey(t)
	now := time.Now().Unix()
	source := "int main(){return 0;}"
	attestation := makePolicyAttestationForTest(t, key, "cpp", "standard", source, now-120, now-60)
	if err := verifyPolicyAttestation("cpp", "standard", source, attestation); err == nil {
		t.Fatal("expired attestation was accepted")
	}
}
