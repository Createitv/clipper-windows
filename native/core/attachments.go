package core

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

const chunkSize = 4 * 1024 * 1024
const maxFileSize = 1024 * 1024 * 1024

func (v *Vault) assetPath(id string) (string, error) {
	if !validID(id) {
		return "", errors.New("invalid asset id")
	}
	p := filepath.Join(v.dir, "assets", id+".bin")
	if e := safePath(p); e != nil {
		return "", e
	}
	return p, nil
}
func (v *Vault) AssetPath(id string) (string, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return "", e
	}
	return v.assetPath(id)
}
func fileType(name string) string {
	ext := strings.ToLower(filepath.Ext(name))
	switch ext {
	case ".svg":
		return "svg"
	case ".png", ".jpg", ".jpeg", ".heic", ".webp", ".gif", ".tiff":
		return "image"
	}
	return "file"
}
func (v *Vault) chunk(r io.Reader, key, clipID string) ([]Object, int64, error) {
	if e := secureDir(filepath.Join(v.dir, "assets")); e != nil {
		return nil, 0, e
	}
	parts := []Object{}
	buf := make([]byte, chunkSize)
	var total int64
	for index := 0; ; index++ {
		n, e := io.ReadFull(r, buf)
		if e == io.EOF {
			break
		}
		if e != nil && e != io.ErrUnexpectedEOF {
			return nil, 0, e
		}
		total += int64(n)
		if limit := v.memberLimit("attachmentBytes"); limit > 0 && total > limit {
			return nil, 0, errors.New("attachment_limit_reached")
		}
		if total > maxFileSize {
			return nil, 0, errors.New("file exceeds 1 GiB")
		}
		id, e := newID()
		if e != nil {
			return nil, 0, e
		}
		context := fmt.Sprintf("file:%s:%s:1:%d", v.env.VaultID, clipID, index)
		box, e := Seal(buf[:n], key, context)
		if e != nil {
			return nil, 0, e
		}
		nonce, _ := keyBytes(box.Nonce, 24)
		cipher, e := decodeBase64(box.Cipher)
		if e != nil {
			return nil, 0, e
		}
		packed := append(nonce, cipher...)
		path, e := v.assetPath(id)
		if e != nil {
			return nil, 0, e
		}
		f, e := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if e != nil {
			return nil, 0, e
		}
		_, e = f.Write(packed)
		if e == nil {
			e = f.Sync()
		}
		ce := f.Close()
		if e == nil {
			e = ce
		}
		if e != nil {
			os.Remove(path)
			return nil, 0, e
		}
		digest := sha256.Sum256(packed)
		parts = append(parts, Object{"id": id, "size": len(packed), "digest": hex.EncodeToString(digest[:]), "context": context, "plainLength": n})
	}
	return parts, total, nil
}
func (v *Vault) fileSnapshot(path, key, name string) (Object, error) {
	if e := safePath(path); e != nil {
		return nil, e
	}
	before, e := os.Lstat(path)
	if e != nil {
		return nil, e
	}
	if !before.Mode().IsRegular() || before.Size() > maxFileSize {
		return nil, errors.New("only regular files at most 1 GiB may be imported")
	}
	f, e := os.Open(path)
	if e != nil {
		return nil, e
	}
	defer f.Close()
	opened, e := f.Stat()
	if e != nil || !os.SameFile(before, opened) {
		return nil, errors.New("file changed")
	}
	id, e := newID()
	if e != nil {
		return nil, e
	}
	parts, size, e := v.chunk(f, key, id)
	if e != nil {
		return nil, e
	}
	after, e := os.Lstat(path)
	if e != nil || !os.SameFile(before, after) || before.Size() != after.Size() || !before.ModTime().Equal(after.ModTime()) || size != before.Size() {
		return nil, errors.New("file changed while reading")
	}
	return v.fileClip(id, key, name, size, parts), nil
}
func (v *Vault) fileClip(id, key, name string, size int64, parts []Object) Object {
	return Object{"id": id, "type": fileType(name), "name": name, "title": name, "size": size, "fileKey": key, "parts": parts, "created": time.Now().UnixMilli(), "favourite": false, "group": "", "author": v.state.DeviceID}
}
func (v *Vault) addClip(c Object) error {
	if err := v.checkClipLimits(c); err != nil {
		return err
	}
	c["version"] = v.tick()
	v.state.Clips[str(c["id"])] = c
	return v.record(Operation{"upsert", c})
}
func (v *Vault) ImportFile(path string) (Object, error) {
	var out Object
	e := v.mutate(func() error {
		key, e := randomKey()
		if e != nil {
			return e
		}
		c, e := v.fileSnapshot(path, key, filepath.Base(path))
		if e != nil {
			return e
		}
		if e = v.addClip(c); e != nil {
			return e
		}
		out, _ = clone(c)
		return nil
	})
	return out, e
}
func (v *Vault) ImportBytes(data []byte, name string) (Object, error) {
	var out Object
	e := v.mutate(func() error {
		memoryLimit := int64(25 * 1024 * 1024)
		if n := v.memberLimit("attachmentBytes"); n > 0 {
			memoryLimit = n
		}
		if int64(len(data)) > memoryLimit {
			return errors.New("memory import exceeds 25 MiB")
		}
		if name == "" {
			return errors.New("filename required")
		}
		id, e := newID()
		if e != nil {
			return e
		}
		key, e := randomKey()
		if e != nil {
			return e
		}
		parts, size, e := v.chunk(bytes.NewReader(data), key, id)
		if e != nil {
			return e
		}
		c := v.fileClip(id, key, name, size, parts)
		if e = v.addClip(c); e != nil {
			return e
		}
		out, _ = clone(c)
		return nil
	})
	return out, e
}

var forbiddenPath = regexp.MustCompile(`[\\:*?"<>|\x00-\x1f]`)
var reservedName = regexp.MustCompile(`(?i)^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)`)

func validateRelative(path string) error {
	if path == "" || strings.HasPrefix(path, "/") {
		return errors.New("unsafe relative path")
	}
	for _, p := range strings.Split(path, "/") {
		if p == "" || p == "." || p == ".." || forbiddenPath.MatchString(p) || strings.HasSuffix(p, ".") || strings.HasSuffix(p, " ") || reservedName.MatchString(p) {
			return errors.New("unsafe relative path")
		}
	}
	return nil
}
func folderEntries(root string) ([]string, error) {
	if e := safePath(root); e != nil {
		return nil, e
	}
	st, e := os.Lstat(root)
	if e != nil {
		return nil, e
	}
	if !st.IsDir() {
		return nil, errors.New("real folder required")
	}
	paths := []string{}
	e = filepath.WalkDir(root, func(path string, d os.DirEntry, e error) error {
		if e != nil {
			return e
		}
		if path == root {
			return nil
		}
		if len(paths) >= 200 {
			return errors.New("folder exceeds 200 entries")
		}
		if !d.IsDir() && !d.Type().IsRegular() {
			return errors.New("folder contains symlink or special file")
		}
		rel, e := filepath.Rel(root, path)
		if e != nil {
			return e
		}
		if e = validateRelative(filepath.ToSlash(rel)); e != nil {
			return e
		}
		paths = append(paths, path)
		return nil
	})
	return paths, e
}
func (v *Vault) ImportFolder(root string) (Object, error) {
	var out Object
	e := v.mutate(func() error {
		entries, e := folderEntries(root)
		if e != nil {
			return e
		}
		key, e := randomKey()
		if e != nil {
			return e
		}
		parts := []Object{}
		files := []Object{}
		dirs := []string{}
		var size int64
		for _, path := range entries {
			st, e := os.Lstat(path)
			if e != nil {
				return e
			}
			rel, _ := filepath.Rel(root, path)
			rel = filepath.ToSlash(rel)
			if st.IsDir() {
				dirs = append(dirs, rel)
				continue
			}
			size += st.Size()
			if size > maxFileSize {
				return errors.New("folder exceeds 1 GiB")
			}
			c, e := v.fileSnapshot(path, key, filepath.Base(path))
			if e != nil {
				return e
			}
			seg := objects(c["parts"])
			files = append(files, Object{"path": rel, "size": c["size"], "start": len(parts), "count": len(seg)})
			parts = append(parts, seg...)
			if len(parts) > 300 {
				return errors.New("folder exceeds 300 chunks")
			}
		}
		after, e := folderEntries(root)
		if e != nil || strings.Join(entries, "\x00") != strings.Join(after, "\x00") {
			return errors.New("folder changed while reading")
		}
		id, e := newID()
		if e != nil {
			return e
		}
		c := v.fileClip(id, key, filepath.Base(filepath.Clean(root)), size, parts)
		c["type"] = "folder"
		c["files"] = files
		c["directories"] = dirs
		raw, e := Marshal(c)
		if e != nil {
			return e
		}
		if len(raw) > 60000 {
			return errors.New("folder manifest exceeds 60000 bytes")
		}
		if e = v.addClip(c); e != nil {
			return e
		}
		out, _ = clone(c)
		return nil
	})
	return out, e
}
