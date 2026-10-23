package syncer

import (
	"clipweave.local/device/core"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"runtime"
	"strings"
	"time"
	"unicode"
)

type DeviceInfo struct {
	Name      string `json:"name"`
	Model     string `json:"model"`
	Platform  string `json:"platform"`
	OSVersion string `json:"osVersion"`
}

func cleanDeviceText(s string) string {
	s = strings.TrimSpace(strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return -1
		}
		return r
	}, s))
	r := []rune(s)
	if len(r) > 80 {
		r = r[:80]
	}
	return string(r)
}
func normalizeDeviceInfo(d DeviceInfo) DeviceInfo {
	d.Name = cleanDeviceText(d.Name)
	d.Model = cleanDeviceText(d.Model)
	d.OSVersion = cleanDeviceText(d.OSVersion)
	switch d.Platform {
	case "iphone", "ipad", "mac", "windows", "android", "harmonyos", "linux", "web", "unknown":
	default:
		d.Platform = "unknown"
	}
	if d.Name == "" {
		d.Name = d.Model
	}
	if d.Name == "" {
		d.Name = "Clipper"
	}
	return d
}
func defaultDeviceInfo() DeviceInfo {
	name, _ := os.Hostname()
	platform := runtime.GOOS
	if platform == "darwin" {
		platform = "mac"
	}
	return normalizeDeviceInfo(DeviceInfo{Name: name, Platform: platform, OSVersion: runtime.GOOS})
}
func decodeDeviceInfo(raw []byte) DeviceInfo {
	var d DeviceInfo
	if json.Unmarshal(raw, &d) == nil && d.Name != "" {
		return normalizeDeviceInfo(d)
	}
	return normalizeDeviceInfo(DeviceInfo{Name: string(raw), Platform: "unknown"})
}
func (e *Engine) SetDeviceInfo(d DeviceInfo) {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.deviceInfo = normalizeDeviceInfo(d)
}
func (e *Engine) deviceNameBytes() []byte { raw, _ := json.Marshal(e.deviceInfo); return raw }

// Updates the current device's encrypted label without reauthenticating or
// changing its signing identity. Older servers remain usable during rollout.
func (e *Engine) publishDeviceInfo(ctx context.Context, a *API, token string) {
	st, err := e.vault.Snapshot()
	if err != nil {
		return
	}
	plain := e.deviceNameBytes()
	sum := sha256.Sum256(plain)
	hash := hex.EncodeToString(sum[:])
	if st.Config["deviceInfoHash"] == hash {
		return
	}
	sec, err := e.vault.Credentials()
	if err != nil {
		return
	}
	box, err := core.Seal(plain, sec.Key, "device:"+st.DeviceID)
	if err != nil {
		return
	}
	raw, _ := json.Marshal(box)
	child, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	if a.call(child, "POST", "/devices/metadata", token, core.Object{"nameCipher": string(raw)}, nil) == nil {
		_ = e.vault.UpdateConfig(core.Object{"deviceInfoHash": hash})
	}
}
