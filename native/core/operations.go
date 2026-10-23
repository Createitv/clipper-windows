package core

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"net/url"
	"sort"
	"strings"
	"time"
	"unicode/utf8"
)

func (v *Vault) tick() int64 { v.state.Clock++; return v.state.Clock }
func (v *Vault) record(op Operation) error {
	id, e := newID()
	if e != nil {
		return e
	}
	key, e := randomKey()
	if e != nil {
		return e
	}
	plain, e := Marshal(op)
	if e != nil {
		return e
	}
	payload, e := Seal(plain, key, "item:"+v.env.VaultID+":"+id)
	if e != nil {
		return e
	}
	k, _ := keyBytes(key, 32)
	wrapped, e := Seal(k, v.secrets.Key, "item-key:"+v.env.VaultID+":"+id)
	clear(k)
	if e != nil {
		return e
	}
	cipher, e := Marshal(Object{"wrapped": wrapped, "payload": payload})
	if e != nil {
		return e
	}
	// The Worker accepts at most 100000 ASCII bytes in the serialized cipher.
	// Plaintext limits alone cannot bound JSON escaping plus Base64 expansion.
	if len(cipher) > 100000 {
		return errors.New("encrypted operation exceeds sync limit; shorten the content or import it as a file")
	}
	v.state.Sequence++
	v.state.Outbox = append(v.state.Outbox, Packet{ID: id, DeviceID: v.state.DeviceID, Epoch: v.secrets.Epoch, Sequence: v.state.Sequence, Cipher: string(cipher), AssetIDs: []string{}})
	v.activity(op.Kind, v.state.DeviceID)
	return nil
}
func (v *Vault) activity(kind, device string) {
	v.state.Activity = append([]Object{{"kind": kind, "time": time.Now().UnixMilli(), "device": device}}, v.state.Activity...)
	if len(v.state.Activity) > 500 {
		v.state.Activity = v.state.Activity[:500]
	}
}
func (v *Vault) decodeOperation(p Packet) (Operation, error) {
	var op Operation
	var cipher struct {
		Wrapped Box `json:"wrapped"`
		Payload Box `json:"payload"`
	}
	if len(p.Cipher) > 128*1024 {
		return op, errors.New("operation too large")
	}
	if e := decode([]byte(p.Cipher), &cipher); e != nil {
		return op, e
	}
	key, e := Open(cipher.Wrapped, v.secrets.Key, "item-key:"+v.env.VaultID+":"+p.ID)
	if e != nil {
		return op, e
	}
	defer clear(key)
	plain, e := Open(cipher.Payload, enc(key), "item:"+v.env.VaultID+":"+p.ID)
	if e != nil {
		return op, e
	}
	e = decode(plain, &op)
	return op, e
}
func (v *Vault) DecodeOperation(p Packet) (Operation, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return Operation{}, e
	}
	return v.decodeOperation(p)
}
func (v *Vault) ApplyRemote(op Operation, cursor int64) error {
	return v.mutate(func() error {
		c, e := clone(op.Clip)
		if e != nil {
			return e
		}
		id := str(c["id"])
		if !validID(id) || integer(c["version"]) < 0 || str(c["author"]) == "" || (op.Kind != "upsert" && op.Kind != "delete") {
			return errors.New("invalid remote operation")
		}
		if op.Kind == "delete" && c["deleted"] != true {
			return errors.New("invalid tombstone")
		}
		version := integer(c["version"])
		if version > v.state.Clock {
			v.state.Clock = version
		}
		old := v.state.Clips[id]
		if old == nil || (old["deleted"] != true && (c["deleted"] == true || version > integer(old["version"]) || version == integer(old["version"]) && str(c["author"]) > str(old["author"]))) {
			v.state.Clips[id] = c
			v.activity(op.Kind, str(c["author"]))
		}
		v.state.Cursor = cursor
		v.addGroup(str(c["group"]))
		return nil
	})
}
func (v *Vault) AddText(text, html string) (Object, error) {
	var result Object
	e := v.mutate(func() error {
		if text == "" || len(text)+len(html) > 32*1024 {
			return errors.New("text must be nonempty and at most 32 KiB")
		}
		id, e := newID()
		if e != nil {
			return e
		}
		kind := "text"
		if u, e := url.Parse(text); e == nil && u.Scheme != "" {
			kind = "link"
		}
		c := Object{"id": id, "type": kind, "text": text, "title": strings.SplitN(text, "\n", 2)[0], "created": time.Now().UnixMilli(), "favourite": false, "group": "", "version": v.tick(), "author": v.state.DeviceID}
		if html != "" {
			c["html"] = html
		}
		v.state.Clips[id] = c
		if e = v.record(Operation{"upsert", c}); e != nil {
			return e
		}
		result, _ = clone(c)
		return nil
	})
	return result, e
}
func (v *Vault) List(query, kind, group string, favourites bool) ([]Object, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return nil, e
	}
	out := []Object{}
	q := strings.ToLower(query)
	for _, c := range v.state.Clips {
		if c["deleted"] == true || favourites && c["favourite"] != true || kind != "" && str(c["type"]) != kind || group != "" && str(c["group"]) != group {
			continue
		}
		search := fmt.Sprint(c["text"], " ", c["title"], " ", c["name"], " ", c["group"])
		if strings.Contains(strings.ToLower(search), q) {
			out = append(out, c)
		}
	}
	sort.Slice(out, func(i, j int) bool {
		a, b := integer(out[i]["created"]), integer(out[j]["created"])
		if a == b {
			return str(out[i]["id"]) > str(out[j]["id"])
		}
		return a > b
	})
	return clone(out)
}
func (v *Vault) find(id string) (Object, error) {
	c := v.state.Clips[id]
	if c == nil || c["deleted"] == true {
		return nil, errors.New("clip not found")
	}
	return c, nil
}
func (v *Vault) Find(id string) (Object, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return nil, e
	}
	c, e := v.find(id)
	if e != nil {
		return nil, e
	}
	return clone(c)
}
func (v *Vault) edit(id string, fn func(Object)) error {
	return v.mutate(func() error {
		c, e := v.find(id)
		if e != nil {
			return e
		}
		fn(c)
		c["version"] = v.tick()
		c["author"] = v.state.DeviceID
		return v.record(Operation{"upsert", c})
	})
}
func (v *Vault) Favourite(id string) error {
	return v.edit(id, func(c Object) { c["favourite"] = c["favourite"] != true })
}

// SetFavourite sets an explicit value under the same mutation lock as sync.
func (v *Vault) SetFavourite(id string, value bool) error {
	return v.edit(id, func(c Object) { c["favourite"] = value })
}
func (v *Vault) addGroup(name string) {
	if name == "" {
		return
	}
	for _, x := range v.state.Groups {
		if name == x {
			return
		}
	}
	v.state.Groups = append(v.state.Groups, name)
}
func (v *Vault) SetGroup(id, name string) error {
	if name != "" && (strings.TrimSpace(name) == "" || utf8.RuneCountInString(name) > 80) {
		return errors.New("group must contain 1 to 80 characters")
	}
	return v.edit(id, func(c Object) { c["group"] = name; v.addGroup(name) })
}
func (v *Vault) delete(id string) error {
	kept := []Packet{}
	for _, p := range v.state.Outbox {
		op, e := v.decodeOperation(p)
		if e != nil {
			return e
		}
		if str(op.Clip["id"]) != id {
			kept = append(kept, p)
		}
	}
	v.state.Outbox = kept
	c := Object{"id": id, "deleted": true, "version": v.tick(), "author": v.state.DeviceID}
	v.state.Clips[id] = c
	return v.record(Operation{"delete", c})
}
func (v *Vault) Delete(id string) error {
	return v.mutate(func() error {
		if !validID(id) {
			return errors.New("invalid clip id")
		}
		return v.delete(id)
	})
}
func (v *Vault) DeleteExpired() (int, error) {
	n := 0
	e := v.mutate(func() error {
		days := integer(v.state.Config["retentionDays"])
		if days <= 0 {
			return nil
		}
		if days > 36500 {
			return errors.New("invalid retention period")
		}
		cutoff := time.Now().Add(-time.Duration(days) * 24 * time.Hour).UnixMilli()
		ids := []string{}
		for id, c := range v.state.Clips {
			if c["deleted"] != true && c["favourite"] != true && integer(c["created"]) < cutoff {
				ids = append(ids, id)
			}
		}
		for _, id := range ids {
			if e := v.delete(id); e != nil {
				return e
			}
			n++
		}
		return nil
	})
	if e != nil {
		return 0, e
	}
	return n, nil
}

func (v *Vault) AddGroup(name string) error {
	return v.mutate(func() error {
		if strings.TrimSpace(name) == "" || utf8.RuneCountInString(name) > 80 {
			return errors.New("group must contain 1 to 80 characters")
		}
		for _, existing := range v.state.Groups {
			if existing == name {
				return errors.New("group already exists")
			}
		}
		v.addGroup(name)
		return v.recordGroup(name)
	})
}

// Group definitions use invisible tombstones in the existing encrypted operation
// stream. Older clients already merge their group field and hide deleted items.
// This keeps empty groups compatible without exposing names to the server.
func (v *Vault) recordGroup(name string) error {
	id := fmt.Sprintf("group_%x", sha256.Sum256([]byte(name)))
	if v.state.Clips[id] != nil {
		return nil
	}
	c := Object{"id": id, "type": "group", "group": name, "deleted": true, "version": v.tick(), "author": v.state.DeviceID}
	if err := v.record(Operation{Kind: "delete", Clip: c}); err != nil {
		return err
	}
	v.state.Clips[id] = c
	return nil
}

// EnsureGroupsSynced migrates existing local-only groups once, and is retry-safe.
func (v *Vault) EnsureGroupsSynced() error {
	v.mu.Lock()
	if err := v.require(); err != nil {
		v.mu.Unlock()
		return err
	}
	missing := false
	for _, name := range v.state.Groups {
		id := fmt.Sprintf("group_%x", sha256.Sum256([]byte(name)))
		if v.state.Clips[id] == nil {
			missing = true
			break
		}
	}
	v.mu.Unlock()
	if !missing {
		return nil
	}
	return v.mutate(func() error {
		for _, name := range v.state.Groups {
			if err := v.recordGroup(name); err != nil {
				return err
			}
		}
		return nil
	})
}
