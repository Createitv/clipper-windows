// Package protocol contains versioned wire signing, never content keys.
package protocol

import (
	"bytes"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"strconv"
	"time"
)

func marshal(v any) []byte {
	var b bytes.Buffer
	e := json.NewEncoder(&b)
	e.SetEscapeHTML(false)
	_ = e.Encode(v)
	return bytes.TrimSuffix(b.Bytes(), []byte{'\n'})
}
func sum(b []byte) string { x := sha256.Sum256(b); return hex.EncodeToString(x[:]) }
func SignRequest(r *http.Request, body []byte, token string, key ed25519.PrivateKey, now time.Time, nonce string) {
	stamp := strconv.FormatInt(now.Unix(), 10)
	r.Header.Set("X-Cliper-Time", stamp)
	r.Header.Set("X-Cliper-Nonce", nonce)
	msg := marshal([]any{"cliper-request-v1", sum([]byte(token)), r.Method, r.URL.RequestURI(), sum(body), stamp, nonce})
	r.Header.Set("X-Cliper-Proof", base64.StdEncoding.EncodeToString(ed25519.Sign(key, msg)))
}
