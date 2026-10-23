package core

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"strings"
)

// OpenVault follows only a validated account-cache selector. The original vault
// remains at the root and is never overwritten by signing into another account.
func OpenVault(root string) (*Vault, error) {
	v := NewVault(root)
	raw, err := readRegular(filepath.Join(root, "active-account"), 128)
	if os.IsNotExist(err) {
		return v, nil
	}
	if err != nil {
		return nil, err
	}
	name := string(raw)
	if len(name) != 64 || strings.Trim(name, "0123456789abcdef") != "" {
		return nil, errors.New("账户缓存索引无效")
	}
	dir := filepath.Join(root, "accounts", name)
	if err = safePath(dir); err != nil {
		return nil, err
	}
	v.dir = dir
	return v, nil
}

// SelectCloudCache isolates mismatched/locked legacy data without deleting or
// relabeling it. Existing account caches must unlock before activation.
func (v *Vault) SelectCloudCache(account, vaultID, pass string) error {
	if account == "" {
		return errors.New("缺少云端账户身份")
	}
	v.mu.Lock()
	defer v.mu.Unlock()
	sum := sha256.Sum256([]byte(account + "\x00" + vaultID))
	name := hex.EncodeToString(sum[:])
	dir := filepath.Join(v.root, "accounts", name)
	if err := secureDir(dir); err != nil {
		return err
	}
	next := NewVault(dir)
	if next.Exists() {
		if err := next.Unlock(pass, false); err != nil {
			return errors.New("此账户的缓存无法解锁，请使用原设备凭据或恢复文件")
		}
	}
	marker := filepath.Join(v.root, "active-account")
	if err := safePath(marker); err != nil {
		return err
	}
	f, err := os.CreateTemp(v.root, ".account-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	if _, err = f.WriteString(name); err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	if err = os.Rename(f.Name(), marker); err != nil {
		return err
	}
	v.lock()
	v.dir, v.env, v.secrets, v.state, v.localKey, v.localWrap = next.dir, next.env, next.secrets, next.state, next.localKey, next.localWrap
	return nil
}
