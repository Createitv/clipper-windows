package core

import (
	"errors"
	"path/filepath"
)

func (v *Vault) UnlockAccess(access Access) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	data, e := readRegular(filepath.Join(v.dir, "vault.json"), 48*1024*1024)
	if e != nil {
		return e
	}
	var raw diskStore
	if e = decode(data, &raw); e != nil {
		return e
	}
	if raw.Envelope.VaultID != access.Envelope.VaultID || raw.Envelope.RootPublic != access.Secrets.RootPublic {
		return errors.New("恢复文件与本机资料库不匹配")
	}
	sec := access.Secrets
	sec.RootSecret = ""
	lk, e := Open(raw.LocalKey, sec.Key, "local-key:"+raw.Envelope.VaultID)
	if e != nil {
		return e
	}
	defer clear(lk)
	if len(lk) != 32 {
		return errors.New("invalid local key")
	}
	plain, e := Open(raw.State, enc(lk), "local-state:"+raw.Envelope.VaultID)
	if e != nil {
		return e
	}
	defer clear(plain)
	var state State
	if e = decode(plain, &state); e != nil {
		return e
	}
	if state.Clips == nil || state.DeviceID == "" {
		return errors.New("invalid vault state")
	}
	if _, e = keyBytes(state.DeviceSecret, 64); e != nil {
		return e
	}
	if state.Config == nil {
		state.Config = Object{}
	}
	v.env = raw.Envelope
	v.secrets = sec
	v.localKey = enc(lk)
	v.localWrap = raw.LocalKey
	v.state = state
	return nil
}
