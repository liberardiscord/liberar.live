package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"math/bits"
)

// Proof of work exists for one reason: registering a device must cost something,
// so that farming devices in bulk is expensive. It does nothing against a
// distributed attacker, and the documentation says so rather than pretending
// otherwise.
//
// The client is given a random nonce and must find a counter such that
// SHA-256(nonce || counter) starts with `difficulty` zero bits. The counter is
// encoded big-endian over 8 bytes so both sides agree byte for byte.

const powNonceBytes = 16

func newPoWNonce() (string, error) {
	buf := make([]byte, powNonceBytes)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	return hex.EncodeToString(buf), nil
}

// leadingZeroBits counts the zero bits at the front of a digest.
func leadingZeroBits(sum []byte) int {
	total := 0
	for _, b := range sum {
		n := bits.LeadingZeros8(b)
		total += n
		if n != 8 {
			break
		}
	}
	return total
}

// verifyPoW recomputes the digest for the claimed counter. It is deliberately
// cheap: one hash. All of the cost sits on the client.
func verifyPoW(nonceHex string, counter uint64, difficulty int) bool {
	nonce, err := hex.DecodeString(nonceHex)
	if err != nil {
		return false
	}

	buf := make([]byte, 0, len(nonce)+8)
	buf = append(buf, nonce...)
	var c [8]byte
	binary.BigEndian.PutUint64(c[:], counter)
	buf = append(buf, c[:]...)

	sum := sha256.Sum256(buf)
	return leadingZeroBits(sum[:]) >= difficulty
}

// difficultyFor escalates the cost for a /24 that keeps registering devices.
// A household behind one address registers once; a farm registering hundreds
// pays exponentially more for each additional device.
func difficultyFor(base, max int, recentRegistrations int64) int {
	d := base
	switch {
	case recentRegistrations >= 200:
		d = base + 6
	case recentRegistrations >= 50:
		d = base + 4
	case recentRegistrations >= 10:
		d = base + 2
	}
	if d > max {
		d = max
	}
	return d
}
