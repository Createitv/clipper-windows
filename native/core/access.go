package core

import (
	"crypto/rand"
	"encoding/json"
	"errors"
	"golang.org/x/crypto/argon2"
	"os"
	"path/filepath"
	"unicode/utf8"
)

// Access never crosses the network unencrypted. It is not part of status or logs.
type Access struct {
	Envelope Envelope `json:"envelope"`
	Secrets  Secrets  `json:"secrets"`
}
type RecoveryFile struct {
	Format   string   `json:"format"`
	Envelope Envelope `json:"envelope"`
	Recovery string   `json:"recovery"`
}

func wrapAccess(env Envelope, sec Secrets, pass string) (Envelope, error) {
	if utf8.RuneCountInString(pass) < 16 {
		return env, errors.New("本机安全密钥无效")
	}
	salt := make([]byte, 16)
	if _, err := rand.Read(salt); err != nil {
		return env, err
	}
	raw, err := Marshal(sec)
	if err != nil {
		return env, err
	}
	defer clear(raw)
	env.Salt = enc(salt)
	env.Ops = 3
	env.Memory = 67108864
	env.Password, err = Seal(raw, enc(argon2.IDKey([]byte(pass), salt, 3, 65536, 1, 32)), "password:"+env.VaultID)
	if err != nil {
		return env, err
	}
	// Reuse the same validation as legacy envelopes, including signing-key consistency.
	_, err = UnlockEnvelope(env, pass, false)
	return env, err
}
func (v *Vault) Access(pass string) (Access, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if err := v.require(); err != nil {
		return Access{}, err
	}
	sec, err := UnlockEnvelope(v.env, pass, false)
	return Access{v.env, sec}, err
}

// InstallAccess never replaces another library or discards its history.
func (v *Vault) InstallAccess(access Access, pass string) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	env, err := wrapAccess(access.Envelope, access.Secrets, pass)
	if err != nil {
		return err
	}
	if _, err := os.Lstat(filepath.Join(v.dir, "vault.json")); err == nil {
		if v.localKey == "" {
			return errors.New("请先解锁本机已有资料库，现有内容不会被覆盖")
		}
		if v.env.VaultID != env.VaultID || v.secrets.Key != access.Secrets.Key {
			if v.state.Config["autoProvisional"] == true && len(v.state.Clips) == 0 && len(v.state.Outbox) == 0 && str(v.state.Config["token"]) == "" {
				oldEnv, oldSec, oldState, oldKey, oldWrap := v.env, v.secrets, v.state, v.localKey, v.localWrap
				if err = v.initialize(env, access.Secrets); err != nil {
					v.env, v.secrets, v.state, v.localKey, v.localWrap = oldEnv, oldSec, oldState, oldKey, oldWrap
				}
				return err
			}
			return errors.New("本机与账户资料库不同，请先备份本机内容")
		}
		old := v.env
		v.env = env
		if err = v.save(); err != nil {
			v.env = old
		}
		return err
	} else if !os.IsNotExist(err) {
		return err
	}
	if err = v.initialize(env, access.Secrets); err != nil {
		v.lock()
	}
	return err
}
func (v *Vault) ExportRecovery(pass string) (string, error) {
	access, err := v.Access(pass)
	if err != nil {
		return "", err
	}
	key, err := randomKey()
	if err != nil {
		return "", err
	}
	raw, err := Marshal(access.Secrets)
	if err != nil {
		return "", err
	}
	defer clear(raw)
	access.Envelope.Recovery, err = Seal(raw, key, "recovery:"+access.Envelope.VaultID)
	if err != nil {
		return "", err
	}
	data, err := Marshal(RecoveryFile{"cliper-recovery-v1", access.Envelope, key})
	return string(data), err
}
func ReadRecovery(text string) (Access, error) {
	if len(text) > 32768 {
		return Access{}, errors.New("恢复文件过大")
	}
	var file RecoveryFile
	if json.Unmarshal([]byte(text), &file) != nil || file.Format != "cliper-recovery-v1" {
		return Access{}, errors.New("请选择 Clipper 恢复文件")
	}
	sec, err := UnlockEnvelope(file.Envelope, file.Recovery, true)
	return Access{file.Envelope, sec}, err
}

func (v *Vault) ReplaceableProvisional() bool {
	v.mu.Lock()
	defer v.mu.Unlock()
	return v.localKey != "" && v.state.Config["autoProvisional"] == true && len(v.state.Clips) == 0 && len(v.state.Outbox) == 0 && str(v.state.Config["token"]) == ""
}

func (v *Vault) CheckRecovery(access Access) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	data, err := readRegular(filepath.Join(v.dir, "vault.json"), 48*1024*1024)
	if os.IsNotExist(err) {
		return nil
	}
	if err != nil {
		return err
	}
	var raw diskStore
	if err = decode(data, &raw); err != nil {
		return err
	}
	if raw.Envelope.VaultID != access.Envelope.VaultID || raw.Envelope.RootPublic != access.Secrets.RootPublic {
		return errors.New("恢复文件与本机资料库不匹配")
	}
	key, err := Open(raw.LocalKey, access.Secrets.Key, "local-key:"+raw.Envelope.VaultID)
	clear(key)
	return err
}
