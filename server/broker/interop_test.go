package main

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"
	"strings"
	"testing"
)

// The client signs on Windows with ECDsaCng and this package verifies in Go.
// Compiling proves nothing about whether the two agree on the bytes: a wrong
// SPKI prefix, or a signature encoded as ASN.1 DER instead of the IEEE P1363
// halves, would break every activation while every unit test still passed.
//
// testdata/csharp_interop.json holds material produced by the real
// DeviceIdentity, so the agreement is checked here on every run, including on
// machines that have no .NET at all.
//
// Regenerate with tools/interop/GenerateInteropVector.cs.

type interopVector struct {
	DeviceID        string `json:"device_id"`
	PublicKey       string `json:"public_key"`
	Nonce           string `json:"nonce"`
	Signature       string `json:"signature"`
	SignatureSecond string `json:"signature_second"`
}

func loadInteropVector(t *testing.T) interopVector {
	t.Helper()

	raw, err := os.ReadFile("testdata/csharp_interop.json")
	if err != nil {
		t.Skipf("no interop vector: %v", err)
	}

	var v interopVector
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("interop vector is not readable: %v", err)
	}
	return v
}

// TestCSharpPublicKeyParses covers the hand-assembled SubjectPublicKeyInfo.
// .NET Framework 4.8 has no ExportSubjectPublicKeyInfo, so the client builds the
// DER around the public point from a fixed 26 byte prefix. One wrong byte there
// makes every registration fail with bad_public_key.
func TestCSharpPublicKeyParses(t *testing.T) {
	v := loadInteropVector(t)

	der, err := base64.StdEncoding.DecodeString(v.PublicKey)
	if err != nil {
		t.Fatalf("public key is not base64: %v", err)
	}
	if len(der) != 91 {
		t.Fatalf("a P-256 SubjectPublicKeyInfo is 91 bytes, got %d", len(der))
	}
	if _, err := parseP256PublicKey(der); err != nil {
		t.Fatalf("the client's SPKI does not parse as a P-256 key: %v", err)
	}
}

// TestCSharpDeviceIDMatches checks that both sides derive the same identifier
// from the same key. They compute it independently, and a mismatch would make
// the client ask for challenges under a name the broker never stored.
func TestCSharpDeviceIDMatches(t *testing.T) {
	v := loadInteropVector(t)

	der, err := base64.StdEncoding.DecodeString(v.PublicKey)
	if err != nil {
		t.Fatalf("public key is not base64: %v", err)
	}

	sum := sha256.Sum256(der)
	got := hex.EncodeToString(sum[:16])

	if got != v.DeviceID {
		t.Fatalf("device id disagreement: client says %s, broker derives %s", v.DeviceID, got)
	}
	if len(got) != 32 {
		t.Fatalf("device id must be 32 hex characters, got %d", len(got))
	}
}

// TestCSharpSignatureVerifies is the assertion that matters most: a signature
// this project's Windows client actually produced, accepted by the code path
// that guards /v1/session.
func TestCSharpSignatureVerifies(t *testing.T) {
	v := loadInteropVector(t)

	der, err := base64.StdEncoding.DecodeString(v.PublicKey)
	if err != nil {
		t.Fatalf("public key is not base64: %v", err)
	}

	sig, err := base64.StdEncoding.DecodeString(v.Signature)
	if err != nil {
		t.Fatalf("signature is not base64: %v", err)
	}
	if len(sig) != 64 {
		t.Fatalf("expected IEEE P1363 r||s of 64 bytes, got %d; .NET may be emitting DER", len(sig))
	}

	if !verifySessionSignature(der, v.DeviceID, v.Nonce, v.Signature) {
		t.Fatal("the broker rejected a signature made by the real client")
	}

	// Both signatures over the same message must verify. ECDSA picks a fresh k
	// each time, so this also confirms the second one is not a stale copy.
	if !verifySessionSignature(der, v.DeviceID, v.Nonce, v.SignatureSecond) {
		t.Fatal("the broker rejected the signature made after the DPAPI round trip")
	}
	if v.Signature == v.SignatureSecond {
		t.Fatal("two signatures over the same message are identical: k is not random")
	}
}

// TestCSharpSignatureIsBoundToItsContext confirms the domain separation is real.
// The signed message is domain || device_id || nonce, so a signature must not
// travel to another device, another challenge, or another purpose.
func TestCSharpSignatureIsBoundToItsContext(t *testing.T) {
	v := loadInteropVector(t)

	der, err := base64.StdEncoding.DecodeString(v.PublicKey)
	if err != nil {
		t.Fatalf("public key is not base64: %v", err)
	}

	otherDevice := strings.Repeat("a", 32)
	if verifySessionSignature(der, otherDevice, v.Nonce, v.Signature) {
		t.Fatal("a signature verified under a different device id")
	}

	otherNonce := strings.Repeat("0", len(v.Nonce))
	if verifySessionSignature(der, v.DeviceID, otherNonce, v.Signature) {
		t.Fatal("a signature verified against a different challenge")
	}

	// Flipping one bit of r must be fatal.
	sig, _ := base64.StdEncoding.DecodeString(v.Signature)
	sig[0] ^= 0x01
	if verifySessionSignature(der, v.DeviceID, v.Nonce, base64.StdEncoding.EncodeToString(sig)) {
		t.Fatal("a tampered signature verified")
	}
}

// TestSessionDomainMatchesClient pins the domain string byte for byte. The C#
// side writes it as "droute-session-v1\0"; if either side ever edits its own
// copy, this catches it before a release does.
func TestSessionDomainMatchesClient(t *testing.T) {
	const fromCSharp = "droute-session-v1\x00"
	if sessionDomain != fromCSharp {
		t.Fatalf("domain drift: broker has %q, client has %q", sessionDomain, fromCSharp)
	}
	if !strings.HasSuffix(sessionDomain, "\x00") {
		t.Fatal("the trailing NUL is what separates the domain from the device id")
	}
}
