package syncer

import (
	"clipweave.local/device/core"
	"crypto/x509"
	"encoding/json"
	"errors"
	"time"
)

// The platform must keep this opaque snapshot in device-only secure storage.
// Account passwords are never retained. The pairing private key must survive
// process death so an approval issued while the app is closed can be decrypted.
type pendingSnapshot struct {
	Version    int          `json:"version"`
	Login      pendingLogin `json:"login"`
	PrivateKey []byte       `json:"privateKey,omitempty"`
}

func (e *Engine) ExportPendingSession(deviceKey string) (string, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.pending == nil {
		return "", nil
	}
	e.pending.remembered = true
	copy := *e.pending
	snapshot := pendingSnapshot{Version: 1, Login: copy}
	if copy.Private != nil {
		snapshot.PrivateKey = x509.MarshalPKCS1PrivateKey(copy.Private)
	}
	snapshot.Login.Private = nil
	plain, err := json.Marshal(snapshot)
	if err != nil {
		return "", err
	}
	defer clear(plain)
	box, err := core.Seal(plain, deviceKey, "cliper-pending-login-v1")
	if err != nil {
		return "", err
	}
	data, err := json.Marshal(box)
	return string(data), err
}
func (e *Engine) RestorePendingSession(text, deviceKey string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.pending != nil || text == "" {
		return nil
	}
	if len(text) > 128*1024 {
		return errors.New("登录会话无效")
	}
	var box core.Box
	if err := json.Unmarshal([]byte(text), &box); err != nil {
		return err
	}
	plain, err := core.Open(box, deviceKey, "cliper-pending-login-v1")
	if err != nil {
		return err
	}
	defer clear(plain)
	var saved pendingSnapshot
	if err = json.Unmarshal(plain, &saved); err != nil {
		return err
	}
	p := &saved.Login
	if saved.Version != 1 || p.Token == "" || p.AccountID == "" || p.Challenge == "" {
		return errors.New("登录会话无效")
	}
	if _, err = NewAPI(p.Endpoint); err != nil {
		return err
	}
	if p.Expires <= time.Now().UnixMilli() {
		return nil
	}
	if len(saved.PrivateKey) > 0 {
		p.Private, err = x509.ParsePKCS1PrivateKey(saved.PrivateKey)
		if err != nil {
			return err
		}
		if p.Private.N.BitLen() != 2048 || p.Private.E != 65537 {
			return errors.New("配对密钥无效")
		}
	}
	p.remembered = true
	e.pending = p
	return nil
}
func (e *Engine) PendingPairing() core.Object {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.pending == nil {
		return nil
	}
	return core.Object{"id": e.pending.Pair.ID, "code": e.pending.Pair.Code, "expires": e.pending.Pair.Expires, "fingerprint": e.pending.Pair.Fingerprint, "qrCode": pairingQR(e.pending.Pair.Code, e.pending.Pair.Fingerprint), "email": e.pending.Email, "deviceInfo": e.pending.Pair.DeviceInfo, "created": e.pending.Pair.Created}
}
func (e *Engine) ForgetPendingSession() { e.mu.Lock(); defer e.mu.Unlock(); e.pending = nil }
