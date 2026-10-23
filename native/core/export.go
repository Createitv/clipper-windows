package core

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
)

func (v *Vault) writeClip(c Object, w io.Writer) error {
	switch str(c["type"]) {
	case "text", "link", "html":
		text, ok := c["text"].(string)
		if !ok {
			return errors.New("text clip has no text content")
		}
		_, err := io.WriteString(w, text)
		return err
	}

	var total int64
	for _, p := range objects(c["parts"]) {
		path, e := v.assetPath(str(p["id"]))
		if e != nil {
			return e
		}
		data, e := readRegular(path, chunkSize+40)
		if e != nil {
			return e
		}
		sum := sha256.Sum256(data)
		if len(data) < 40 || hex.EncodeToString(sum[:]) != str(p["digest"]) || int64(len(data)) != integer(p["size"]) {
			return errors.New("attachment integrity check failed")
		}
		plain, e := Open(Box{enc(data[:24]), enc(data[24:])}, str(c["fileKey"]), str(p["context"]))
		if e != nil {
			return e
		}
		if int64(len(plain)) != integer(p["plainLength"]) {
			return errors.New("incomplete attachment chunk")
		}
		total += int64(len(plain))
		if total > integer(c["size"]) {
			return errors.New("attachment size mismatch")
		}
		_, e = w.Write(plain)
		clear(plain)
		if e != nil {
			return e
		}
	}
	if total != integer(c["size"]) {
		return errors.New("attachment incomplete")
	}
	return nil
}
func (v *Vault) ReadPreview(id string) ([]byte, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return nil, e
	}
	c, e := v.find(id)
	if e != nil {
		return nil, e
	}
	if integer(c["size"]) < 0 || integer(c["size"]) > 25*1024*1024 || c["type"] == "folder" {
		return nil, errors.New("preview exceeds 25 MiB or is a folder")
	}
	var b bytes.Buffer
	if e = v.writeClip(c, &b); e != nil {
		return nil, e
	}
	return b.Bytes(), nil
}

// LocalAssetsAvailable reports on-disk chunk presence, not content validity.
// Export still authenticates every chunk before publishing plaintext.
func (v *Vault) LocalAssetsAvailable(id string) (bool, error) {
	v.mu.Lock()
	defer v.mu.Unlock()
	if err := v.require(); err != nil {
		return false, err
	}
	clip, err := v.find(id)
	if err != nil {
		return false, err
	}
	switch str(clip["type"]) {
	case "text", "link", "html":
		return true, nil
	}
	parts := objects(clip["parts"])
	if len(parts) == 0 && integer(clip["size"]) != 0 {
		return false, nil
	}
	for _, part := range parts {
		path, err := v.assetPath(str(part["id"]))
		if err != nil {
			return false, err
		}
		info, err := os.Lstat(path)
		if os.IsNotExist(err) {
			return false, nil
		}
		if err != nil {
			return false, err
		}
		if !info.Mode().IsRegular() || info.Size() != integer(part["size"]) {
			return false, nil
		}
	}
	return true, nil
}

func noTarget(path string) error {
	if e := safePath(path); e != nil {
		return e
	}
	if _, e := os.Lstat(path); !os.IsNotExist(e) {
		return errors.New("target already exists")
	}
	return nil
}
func (v *Vault) exportFile(c Object, target string) error {
	if e := noTarget(target); e != nil {
		return e
	}
	f, e := os.CreateTemp(filepath.Dir(target), ".clipweave-*.partial")
	if e != nil {
		return e
	}
	tmp := f.Name()
	defer os.Remove(tmp)
	e = v.writeClip(c, f)
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
	if e = noTarget(target); e != nil {
		return e
	}
	return publishExport(tmp, target)
}
func (v *Vault) Export(id, target string) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	if e := v.require(); e != nil {
		return e
	}
	c, e := v.find(id)
	if e != nil {
		return e
	}
	if e = noTarget(target); e != nil {
		return e
	}
	if str(c["type"]) != "folder" {
		return v.exportFile(c, target)
	}
	files := objects(c["files"])
	dirs := stringsList(c["directories"])
	for _, p := range dirs {
		if e = validateRelative(p); e != nil {
			return e
		}
	}
	for _, f := range files {
		if e = validateRelative(str(f["path"])); e != nil {
			return e
		}
	}
	stage, e := os.MkdirTemp(filepath.Dir(target), ".clipweave-*.partial")
	if e != nil {
		return e
	}
	defer os.RemoveAll(stage)
	for _, p := range dirs {
		if e = os.MkdirAll(filepath.Join(stage, p), 0700); e != nil {
			return e
		}
	}
	parts := objects(c["parts"])
	for _, f := range files {
		start, count := integer(f["start"]), integer(f["count"])
		if start < 0 || count < 0 || start+count > int64(len(parts)) {
			return errors.New("invalid folder chunk range")
		}
		path := filepath.Join(stage, str(f["path"]))
		if e = os.MkdirAll(filepath.Dir(path), 0700); e != nil {
			return e
		}
		if e = v.exportFile(Object{"fileKey": c["fileKey"], "size": f["size"], "parts": parts[start : start+count]}, path); e != nil {
			return e
		}
	}
	if e = noTarget(target); e != nil {
		return e
	}
	return publishExport(stage, target)
}
func (v *Vault) ClearUnused() (int, error) {
	n := 0
	e := v.mutate(func() error {
		pinned := map[string]bool{}
		pin := func(c Object) {
			for _, p := range objects(c["parts"]) {
				pinned[str(p["id"])] = true
			}
		}
		for _, c := range v.state.Clips {
			if c["deleted"] != true {
				pin(c)
			}
		}
		for _, p := range v.state.Outbox {
			op, e := v.decodeOperation(p)
			if e != nil {
				return e
			}
			pin(op.Clip)
		}
		path := filepath.Join(v.dir, "assets")
		if e := safePath(path); e != nil {
			return e
		}
		entries, e := os.ReadDir(path)
		if os.IsNotExist(e) {
			return nil
		}
		if e != nil {
			return e
		}
		for _, entry := range entries {
			if entry.Type().IsRegular() && strings.HasSuffix(entry.Name(), ".bin") && !pinned[strings.TrimSuffix(entry.Name(), ".bin")] {
				if e = os.Remove(filepath.Join(path, entry.Name())); e != nil {
					return e
				}
				n++
			}
		}
		return nil
	})
	return n, e
}

func publishExport(stage, target string) error { return renameNoReplace(stage, target) }
