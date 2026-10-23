// Package core owns the encrypted, legacy-compatible ClipWeave vault.
package core

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"golang.org/x/crypto/argon2"
	"golang.org/x/crypto/chacha20poly1305"
	"strings"
	"unicode/utf8"
)

type Object = map[string]any
type Box struct {
	Nonce  string `json:"nonce"`
	Cipher string `json:"cipher"`
}
type Envelope struct {
	V          int    `json:"v"`
	VaultID    string `json:"vaultId"`
	Salt       string `json:"salt"`
	Ops        int    `json:"ops"`
	Memory     int    `json:"memory"`
	RootPublic string `json:"rootPublic"`
	Password   Box    `json:"password"`
	Recovery   Box    `json:"recovery"`
}
type Secrets struct {
	Key        string `json:"key"`
	RootPublic string `json:"rootPublic"`
	RootSecret string `json:"rootSecret,omitempty"`
	Epoch      int64  `json:"epoch"`
}

func Marshal(v any) ([]byte, error) {
	var b bytes.Buffer
	e := json.NewEncoder(&b)
	e.SetEscapeHTML(false)
	if err := e.Encode(v); err != nil {
		return nil, err
	}
	return bytes.TrimSuffix(b.Bytes(), []byte{'\n'}), nil
}
func decode(data []byte, v any) error {
	d := json.NewDecoder(bytes.NewReader(data))
	d.UseNumber()
	return d.Decode(v)
}
func decodeBase64(s string) ([]byte, error) { return base64.StdEncoding.DecodeString(s) }
func enc(b []byte) string                   { return base64.StdEncoding.EncodeToString(b) }
func keyBytes(s string, n int) ([]byte, error) {
	b, e := base64.StdEncoding.DecodeString(s)
	if e != nil || len(b) != n {
		return nil, errors.New("invalid key or nonce")
	}
	return b, nil
}
func randomKey() (string, error) { b := make([]byte, 32); _, e := rand.Read(b); return enc(b), e }
func newID() (string, error) {
	b := make([]byte, 16)
	if _, e := rand.Read(b); e != nil {
		return "", e
	}
	b[6] = (b[6] & 15) | 64
	b[8] = (b[8] & 63) | 128
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[:4], b[4:6], b[6:8], b[8:10], b[10:]), nil
}
func signingPair() (string, string, error) {
	p, s, e := ed25519.GenerateKey(rand.Reader)
	return enc(p), enc(s), e
}
func Seal(plain []byte, key, context string) (Box, error) {
	k, e := keyBytes(key, 32)
	if e != nil {
		return Box{}, e
	}
	defer clear(k)
	a, e := chacha20poly1305.NewX(k)
	if e != nil {
		return Box{}, e
	}
	n := make([]byte, 24)
	if _, e = rand.Read(n); e != nil {
		return Box{}, e
	}
	return Box{enc(n), enc(a.Seal(nil, n, plain, []byte("clipweave:v1:"+context)))}, nil
}
func Open(box Box, key, context string) ([]byte, error) {
	if len(box.Cipher) > 64*1024*1024 {
		return nil, errors.New("ciphertext exceeds size limit")
	}
	k, e := keyBytes(key, 32)
	if e != nil {
		return nil, e
	}
	defer clear(k)
	n, e := keyBytes(box.Nonce, 24)
	if e != nil {
		return nil, e
	}
	c, e := base64.StdEncoding.DecodeString(box.Cipher)
	if e != nil {
		return nil, e
	}
	a, e := chacha20poly1305.NewX(k)
	if e != nil {
		return nil, e
	}
	return a.Open(nil, n, c, []byte("clipweave:v1:"+context))
}
func Sign(message []byte, secret string) (string, error) {
	k, e := keyBytes(secret, ed25519.PrivateKeySize)
	if e != nil {
		return "", e
	}
	defer clear(k)
	return enc(ed25519.Sign(k, message)), nil
}
func Verify(message []byte, signature, public string) bool {
	p, e := keyBytes(public, 32)
	if e != nil {
		return false
	}
	s, e := keyBytes(signature, 64)
	return e == nil && ed25519.Verify(p, message, s)
}
func UnlockEnvelope(env Envelope, pass string, recovery bool) (Secrets, error) {
	var sec Secrets
	if env.V != 1 || env.Ops != 3 || env.Memory != 67108864 || env.VaultID == "" {
		return sec, errors.New("unsupported envelope parameters")
	}
	salt, e := keyBytes(env.Salt, 16)
	if e != nil {
		return sec, e
	}
	key := pass
	name := "recovery"
	box := env.Recovery
	if !recovery {
		key = enc(argon2.IDKey([]byte(pass), salt, 3, 65536, 1, 32))
		name = "password"
		box = env.Password
	}
	b, e := Open(box, key, name+":"+env.VaultID)
	if e != nil {
		return sec, errors.New("unable to unlock vault")
	}
	defer clear(b)
	if e = decode(b, &sec); e != nil {
		return Secrets{}, e
	}
	if sec.RootPublic != env.RootPublic || sec.Epoch < 1 {
		return Secrets{}, errors.New("envelope identity mismatch")
	}
	if _, e = keyBytes(sec.Key, 32); e != nil {
		return Secrets{}, e
	}
	if _, e = keyBytes(sec.RootPublic, 32); e != nil {
		return Secrets{}, e
	}
	signingSecret, e := keyBytes(sec.RootSecret, 64)
	if e != nil {
		return Secrets{}, e
	}
	defer clear(signingSecret)
	expected := ed25519.NewKeyFromSeed(signingSecret[:32])
	defer clear(expected)
	if !bytes.Equal(expected, signingSecret) || enc(expected[32:]) != sec.RootPublic {
		return Secrets{}, errors.New("envelope signing identity mismatch")
	}
	return sec, nil
}
func createEnvelope(pass, id string) (Envelope, Secrets, string, error) {
	var env Envelope
	var sec Secrets
	if utf8.RuneCountInString(pass) < 16 {
		return env, sec, "", errors.New("passphrase must contain at least 16 characters")
	}
	pub, secret, e := signingPair()
	if e != nil {
		return env, sec, "", e
	}
	key, e := randomKey()
	if e != nil {
		return env, sec, "", e
	}
	recovery, e := randomKey()
	if e != nil {
		return env, sec, "", e
	}
	salt := make([]byte, 16)
	if _, e = rand.Read(salt); e != nil {
		return env, sec, "", e
	}
	sec = Secrets{key, pub, secret, 1}
	payload, e := Marshal(sec)
	if e != nil {
		return env, sec, "", e
	}
	defer clear(payload)
	pw, e := Seal(payload, enc(argon2.IDKey([]byte(pass), salt, 3, 65536, 1, 32)), "password:"+id)
	if e != nil {
		return env, sec, "", e
	}
	rec, e := Seal(payload, recovery, "recovery:"+id)
	env = Envelope{1, id, enc(salt), 3, 67108864, pub, pw, rec}
	return env, sec, recovery, e
}
func str(v any) string { s, _ := v.(string); return s }
func integer(v any) int64 {
	switch x := v.(type) {
	case json.Number:
		n, _ := x.Int64()
		return n
	case int:
		return int64(x)
	case int64:
		return x
	case float64:
		return int64(x)
	}
	return 0
}
func objects(v any) []Object {
	var out []Object
	switch xs := v.(type) {
	case []Object:
		return xs
	case []any:
		for _, x := range xs {
			if m, ok := x.(map[string]any); ok {
				out = append(out, m)
			}
		}
	}
	return out
}
func stringsList(v any) []string {
	switch x := v.(type) {
	case []string:
		return x
	case []any:
		r := make([]string, 0, len(x))
		for _, v := range x {
			if s, ok := v.(string); ok {
				r = append(r, s)
			}
		}
		return r
	}
	return nil
}
func clone[T any](v T) (T, error) {
	var out T
	b, e := Marshal(v)
	if e == nil {
		e = decode(b, &out)
	}
	return out, e
}
func validID(id string) bool {
	return id != "" && len(id) <= 128 && strings.IndexFunc(id, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '_' || r == '-')
	}) < 0
}
