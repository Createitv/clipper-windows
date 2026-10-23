package core

import (
	"errors"
	"io"
	"os"
	"path/filepath"
	"sync"
)

type Packet struct {
	ID        string   `json:"id"`
	DeviceID  string   `json:"deviceId"`
	Epoch     int64    `json:"epoch"`
	Sequence  int64    `json:"sequence"`
	Cipher    string   `json:"cipher"`
	AssetIDs  []string `json:"assetIds"`
	Signature string   `json:"signature,omitempty"`
}
type Operation struct {
	Kind string `json:"kind"`
	Clip Object `json:"clip"`
}
type State struct {
	DeviceID     string            `json:"deviceId"`
	DevicePublic string            `json:"devicePublic"`
	DeviceSecret string            `json:"deviceSecret"`
	Clips        map[string]Object `json:"clips"`
	Outbox       []Packet          `json:"outbox"`
	Cursor       int64             `json:"cursor"`
	Clock        int64             `json:"clock"`
	Sequence     int64             `json:"sequence"`
	Config       Object            `json:"config"`
	Groups       []string          `json:"groups"`
	Activity     []Object          `json:"activity"`
	Ingress      map[string]string `json:"ingress,omitempty"`
}
type Vault struct {
	mu        sync.Mutex
	dir       string
	root      string
	env       Envelope
	secrets   Secrets
	state     State
	localKey  string
	localWrap Box
}
type diskStore struct {
	Envelope Envelope `json:"envelope"`
	LocalKey Box      `json:"localKey"`
	State    Box      `json:"state"`
}

func NewVault(dir string) *Vault { return &Vault{dir: filepath.Clean(dir), root: filepath.Clean(dir)} }
func (v *Vault) Exists() bool {
	v.mu.Lock()
	defer v.mu.Unlock()
	_, e := os.Lstat(filepath.Join(v.dir, "vault.json"))
	return e == nil
}
func (v *Vault) Unlocked() bool { v.mu.Lock(); defer v.mu.Unlock(); return v.localKey != "" }
func (v *Vault) require() error {
	if v.localKey == "" {
		return errors.New("vault is locked")
	}
	return nil
}
func safePath(path string) error {
	abs, e := filepath.Abs(path)
	if e != nil {
		return e
	}
	for p := abs; ; p = filepath.Dir(p) {
		st, e := os.Lstat(p)
		if e != nil && !os.IsNotExist(e) {
			return e
		}
		if e == nil && st.Mode()&os.ModeSymlink != 0 {
			return errors.New("symlink path rejected")
		}
		if filepath.Dir(p) == p {
			break
		}
	}
	return nil
}
func secureDir(path string) error {
	if e := safePath(path); e != nil {
		return e
	}
	if e := os.MkdirAll(path, 0700); e != nil {
		return e
	}
	return os.Chmod(path, 0700)
}
func readRegular(path string, max int64) ([]byte, error) {
	if e := safePath(path); e != nil {
		return nil, e
	}
	st, e := os.Lstat(path)
	if e != nil {
		return nil, e
	}
	if !st.Mode().IsRegular() || st.Size() > max {
		return nil, errors.New("invalid file or size limit exceeded")
	}
	f, e := os.Open(path)
	if e != nil {
		return nil, e
	}
	defer f.Close()
	actual, e := f.Stat()
	if e != nil || !os.SameFile(st, actual) {
		return nil, errors.New("file changed")
	}
	b, e := io.ReadAll(io.LimitReader(f, max+1))
	if e != nil {
		return nil, e
	}
	if int64(len(b)) > max {
		return nil, errors.New("file exceeds size limit")
	}
	return b, nil
}
func (v *Vault) save() error {
	if e := secureDir(v.dir); e != nil {
		return e
	}
	target := filepath.Join(v.dir, "vault.json")
	if e := safePath(target); e != nil {
		return e
	}
	if st, e := os.Lstat(target); e == nil && !st.Mode().IsRegular() {
		return errors.New("invalid vault file")
	}
	plain, e := Marshal(v.state)
	if e != nil {
		return e
	}
	defer clear(plain)
	if len(plain) > 32*1024*1024 {
		return errors.New("vault state exceeds 32 MiB")
	}
	box, e := Seal(plain, v.localKey, "local-state:"+v.env.VaultID)
	if e != nil {
		return e
	}
	data, e := Marshal(diskStore{v.env, v.localWrap, box})
	if e != nil {
		return e
	}
	f, e := os.CreateTemp(v.dir, ".vault-*.tmp")
	if e != nil {
		return e
	}
	name := f.Name()
	defer os.Remove(name)
	if e = f.Chmod(0600); e == nil {
		_, e = f.Write(data)
	}
	if e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e == nil {
		e = ce
	}
	if e != nil {
		return e
	}
	if e = os.Rename(name, target); e != nil {
		return e
	}
	if d, e := os.Open(v.dir); e == nil {
		d.Sync()
		d.Close()
	}
	return nil
}
func (v *Vault) initialize(env Envelope, sec Secrets) error {
	pub, secret, e := signingPair()
	if e != nil {
		return e
	}
	id, e := newID()
	if e != nil {
		return e
	}
	lk, e := randomKey()
	if e != nil {
		return e
	}
	raw, e := keyBytes(lk, 32)
	if e != nil {
		return e
	}
	wrap, e := Seal(raw, sec.Key, "local-key:"+env.VaultID)
	clear(raw)
	if e != nil {
		return e
	}
	sec.RootSecret = ""
	v.env = env
	v.secrets = sec
	v.localKey = lk
	v.localWrap = wrap
	v.state = State{DeviceID: id, DevicePublic: pub, DeviceSecret: secret, Clips: map[string]Object{}, Outbox: []Packet{}, Config: Object{}, Groups: []string{}, Activity: []Object{}}
	return v.save()
}
func (v *Vault) Create(pass string) (string, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if _, e := os.Lstat(filepath.Join(v.dir, "vault.json")); !os.IsNotExist(e) {
		return "", errors.New("local vault already exists")
	}
	id, e := newID()
	if e != nil {
		return "", e
	}
	env, sec, recovery, e := createEnvelope(pass, id)
	if e != nil {
		return "", e
	}
	if e = v.initialize(env, sec); e != nil {
		v.lock()
		return "", e
	}
	return recovery, nil
}
func (v *Vault) Unlock(pass string, recovery bool) error {
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
	sec, e := UnlockEnvelope(raw.Envelope, pass, recovery)
	if e != nil {
		return e
	}
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
func (v *Vault) Join(env Envelope, pass string, recovery bool) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	if v.localKey == "" {
		if _, e := os.Lstat(filepath.Join(v.dir, "vault.json")); !os.IsNotExist(e) {
			return errors.New("unlock existing vault before joining")
		}
	}
	if env.VaultID != v.env.VaultID && (len(v.state.Clips) > 0 || len(v.state.Outbox) > 0) {
		return errors.New("export and back up local history and pending operations before joining")
	}
	// Joining initializes device state, so even same-vault pending history must be preserved.
	if len(v.state.Clips) > 0 || len(v.state.Outbox) > 0 {
		return errors.New("local history or pending operations already exist")
	}
	sec, e := UnlockEnvelope(env, pass, recovery)
	if e != nil {
		return e
	}
	oldEnv, oldSec, oldState, oldLK, oldWrap := v.env, v.secrets, v.state, v.localKey, v.localWrap
	if e = v.initialize(env, sec); e != nil {
		v.env, v.secrets, v.state, v.localKey, v.localWrap = oldEnv, oldSec, oldState, oldLK, oldWrap
	}
	return e
}
func (v *Vault) lock() {
	v.secrets = Secrets{}
	v.state = State{}
	v.localKey = ""
	v.localWrap = Box{}
}
func (v *Vault) Lock() { v.mu.Lock(); defer v.mu.Unlock(); v.lock() }
func (v *Vault) Snapshot() (State, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return State{}, e
	}
	return clone(v.state)
}
func (v *Vault) Envelope() (Envelope, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return Envelope{}, e
	}
	return v.env, nil
}
func (v *Vault) Credentials() (Secrets, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return Secrets{}, e
	}
	return v.secrets, nil
}
func (v *Vault) mutate(fn func() error) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return e
	}
	old, e := clone(v.state)
	if e != nil {
		return e
	}
	if e = fn(); e == nil {
		e = v.save()
	}
	if e != nil {
		v.state = old
	}
	return e
}
func (v *Vault) UpdateConfig(c Object) error {
	return v.mutate(func() error {
		copied, err := clone(c)
		if err != nil {
			return err
		}
		for k, x := range copied {
			v.state.Config[k] = x
		}
		return nil
	})
}
func (v *Vault) Pending() ([]Packet, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return nil, e
	}
	return clone(v.state.Outbox)
}
func (v *Vault) Acknowledge(id string) error {
	return v.mutate(func() error {
		p := v.state.Outbox[:0]
		for _, x := range v.state.Outbox {
			if x.ID != id {
				p = append(p, x)
			}
		}
		v.state.Outbox = p
		return nil
	})
}
func (v *Vault) SetCursor(cursor int64) error {
	return v.mutate(func() error { v.state.Cursor = cursor; return nil })
}
func (v *Vault) MarkOperationAssets(id string, ids []string) error {
	return v.mutate(func() error {
		for i := range v.state.Outbox {
			if v.state.Outbox[i].ID == id {
				v.state.Outbox[i].AssetIDs = append([]string{}, ids...)
				return nil
			}
		}
		return errors.New("operation not found")
	})
}
