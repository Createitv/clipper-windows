// Package bridge exposes the same bounded JSON API to all native UI adapters.
// It intentionally exports only gomobile-supported string/primitive signatures.
package bridge

import (
	"clipweave.local/device/core"
	syncer "clipweave.local/device/sync"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type Client struct {
	mu             sync.Mutex
	vault          *core.Vault
	engine         *syncer.Engine
	ctx            context.Context
	cancel         context.CancelFunc
	wake           chan struct{}
	done           chan struct{}
	file           *os.File
	initErr        error
	closed         bool
	requireAccount bool
}

func NewClient(directory string) *Client {
	ctx, cancel := context.WithCancel(context.Background())
	c := &Client{ctx: ctx, cancel: cancel, wake: make(chan struct{}, 1), done: make(chan struct{}), requireAccount: true}
	if directory == "" {
		c.initErr = errors.New("资料库目录不能为空")
		close(c.done)
		return c
	}
	if err := os.MkdirAll(directory, 0700); err != nil {
		c.initErr = err
		close(c.done)
		return c
	}
	canonical, err := filepath.EvalSymlinks(directory)
	if err != nil {
		c.initErr = err
		close(c.done)
		return c
	}
	lockPath := filepath.Join(canonical, ".session.lock")
	if info, er := os.Lstat(lockPath); er == nil && info.Mode()&os.ModeSymlink != 0 {
		c.initErr = errors.New("资料库锁路径不安全")
		close(c.done)
		return c
	}
	c.file, err = os.OpenFile(lockPath, os.O_RDWR|os.O_CREATE, 0600)
	if err == nil {
		err = lockFile(c.file)
	}
	if err != nil {
		if c.file != nil {
			c.file.Close()
			c.file = nil
		}
		c.initErr = errors.New("此资料库正在由另一个客户端使用，请先退出旧进程")
		close(c.done)
		return c
	}
	c.vault, err = core.OpenVault(canonical)
	if err != nil {
		c.initErr = err
		unlockFile(c.file)
		c.file.Close()
		c.file = nil
		close(c.done)
		return c
	}
	c.engine = syncer.NewEngine(c.vault)
	go c.background()
	return c
}
func (c *Client) Close() {
	c.cancel()
	<-c.done
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return
	}
	c.closed = true
	if c.vault != nil {
		c.vault.Lock()
	}
	if c.file != nil {
		unlockFile(c.file)
		c.file.Close()
		c.file = nil
	}
}
func (c *Client) kick() {
	select {
	case c.wake <- struct{}{}:
	default:
	}
}
func (c *Client) background() {
	defer close(c.done)
	tick := time.NewTicker(30 * time.Second)
	defer tick.Stop()
	for {
		select {
		case <-c.ctx.Done():
			return
		case <-tick.C:
		case <-c.wake:
			timer := time.NewTimer(time.Second)
			select {
			case <-c.ctx.Done():
				timer.Stop()
				return
			case <-timer.C:
			}
		}
		if !c.mu.TryLock() {
			continue
		}
		if !c.closed && c.vault.Unlocked() {
			ctx, cancel := context.WithTimeout(c.ctx, 12*time.Second)
			st, snapshotErr := c.vault.Snapshot()
			if !c.requireAccount || (snapshotErr == nil && syncer.SessionAuthorized(st.Config)) {
				_, _ = c.vault.DeleteExpired()
			}
			_ = c.engine.Sync(ctx)
			cancel()
		}
		c.mu.Unlock()
	}
}

type request struct {
	DeviceInfo *syncer.DeviceInfo `json:"deviceInfo"`
	ID         any                `json:"id"`
	Action     string             `json:"action"`
	Passphrase string             `json:"passphrase"`
	Recovery   bool               `json:"recovery"`
	Confirm    bool               `json:"confirm"`
	Query      string             `json:"query"`
	Kind       string             `json:"kind"`
	Group      string             `json:"group"`
	Favourites bool               `json:"favourites"`
	Text       string             `json:"text"`
	HTML       string             `json:"html"`
	Path       string             `json:"path"`
	Name       string             `json:"name"`
	Base64     string             `json:"base64"`
	ClipID     string             `json:"clipId"`
	PacketID   string             `json:"packetId"`
	DeviceID   string             `json:"deviceId"`
	Endpoint   string             `json:"endpoint"`
	Email      string             `json:"email"`
	Code       string             `json:"code"`
	Password   string             `json:"password"`
	Values     core.Object        `json:"values"`
}

func (c *Client) Invoke(input string) (output string) {
	response := core.Object{"ok": false}
	defer func() {
		if recover() != nil {
			response = core.Object{"ok": false, "error": "请求无法处理，资料库仍保留"}
			b, _ := core.Marshal(response)
			output = string(b)
		}
	}()
	if len(input) > 36*1024*1024 {
		response["error"] = "请求过大"
		b, _ := core.Marshal(response)
		return string(b)
	}
	var r request
	d := json.NewDecoder(strings.NewReader(input))
	d.UseNumber()
	err := d.Decode(&r)
	if err != nil || r.Action == "" {
		response["error"] = "请求格式不正确"
		b, _ := core.Marshal(response)
		return string(b)
	}
	response["id"] = r.ID
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.initErr != nil {
		err = c.initErr
	} else if c.closed {
		err = errors.New("客户端已关闭")
	} else {
		var data any
		ctx, cancel := context.WithTimeout(c.ctx, 60*time.Second)
		defer cancel()
		data, err = c.dispatch(ctx, r)
		if data != nil {
			response["data"] = data
		}
	}
	if err != nil {
		response["error"] = err.Error()
	} else {
		response["ok"] = true
	}
	b, marshalErr := core.Marshal(response)
	if marshalErr != nil {
		return `{"ok":false,"error":"无法编码响应"}`
	}
	return string(b)
}
func (c *Client) status() core.Object {
	result := core.Object{"exists": c.vault.Exists(), "unlocked": c.vault.Unlocked(), "status": "资料库已锁定", "pending": 0, "groups": []string{}, "config": core.Object{}}
	result["pendingPairing"] = c.engine.PendingPairing()
	if !c.vault.Unlocked() {
		return result
	}
	st, err := c.vault.Snapshot()
	if err != nil {
		return result
	}
	env, err := c.vault.Envelope()
	if err != nil {
		return result
	}
	result["vaultId"] = env.VaultID
	result["deviceId"] = st.DeviceID
	result["status"] = c.engine.Status()
	result["lastSuccessAt"] = c.engine.LastSuccess()
	result["pending"] = len(st.Outbox)
	result["groups"] = st.Groups
	result["config"] = publicConfig(st.Config)
	return result
}
func publicConfig(config core.Object) core.Object {
	out := core.Object{"captureEnabled": false, "captureFiles": false, "directPaste": false, "retentionDays": 0, "excludedApps": "com.1password.1password\ncom.agilebits.onepassword7\ncom.bitwarden.desktop\norg.keepassxc.keepassxc\ncom.apple.Passwords\n1Password.exe\nBitwarden.exe\nKeePassXC.exe"}
	for _, k := range []string{"captureEnabled", "captureFiles", "directPaste", "retentionDays", "excludedApps", "endpoint", "email", "accountId", "accountName", "sessionExpires", "accountDeletionRequest", "membership"} {
		if v, ok := config[k]; ok {
			out[k] = v
		}
	}
	out["authenticated"] = syncer.SessionAuthorized(config)
	out["connected"] = syncer.CloudReady(config)
	out["cloudRequired"] = true
	out["cloudSyncedAt"] = config["cloudSyncedAt"]
	return out
}
func value(x any) string                    { s, _ := x.(string); return s }
func sanitize(clip core.Object) core.Object { delete(clip, "fileKey"); return clip }
func (c *Client) dispatch(ctx context.Context, r request) (any, error) {
	if r.Action == "requireAccount" {
		if r.DeviceInfo != nil {
			c.engine.SetDeviceInfo(*r.DeviceInfo)
		}
		c.requireAccount = true
		return c.status(), nil
	}
	if c.requireAccount {
		switch r.Action {
		case "status", "sync", "unlock", "lock", "requestCode", "connect", "autoConnect", "passwordConnect", "completePairing", "inspectPairing", "cancelPairing", "exportPendingSession", "restorePendingSession", "forgetPendingSession", "renewPairing", "validateRecovery", "importRecovery", "disconnect":
		default:
			st, err := c.vault.Snapshot()
			if err != nil || !syncer.CloudReady(st.Config) {
				return nil, errors.New("请先登录并完成云端同步")
			}
		}
	}
	switch r.Action {
	case "createAuto":
		if c.requireAccount {
			return nil, errors.New("请登录账户，由云端创建资料库")
		}
		if _, err := c.vault.Create(r.Passphrase); err != nil {
			return nil, err
		}
		recovery, err := c.vault.ExportRecovery(r.Passphrase)
		return core.Object{"recovery": recovery}, err
	case "exportPendingSession":
		return c.engine.ExportPendingSession(r.Passphrase)
	case "restorePendingSession":
		return nil, c.engine.RestorePendingSession(r.Text, r.Passphrase)
	case "forgetPendingSession":
		c.engine.ForgetPendingSession()
		return nil, nil
	case "passwordConnect":
		return c.engine.PasswordConnect(ctx, r.Endpoint, r.Email, r.Code, r.Password, r.Passphrase)
	case "autoConnect":
		return c.engine.AutoConnect(ctx, r.Endpoint, r.Email, r.Code, r.Passphrase)
	case "renewPairing":
		return c.engine.RenewPairing(ctx)
	case "completePairing":
		return c.engine.CompletePairing(ctx, r.Passphrase)
	case "inspectPairing":
		return c.engine.InspectPairing(ctx, r.Code)
	case "approvePairing":
		return c.engine.ApprovePairing(ctx, r.Code, r.Passphrase, r.Text)
	case "approvePairingQR":
		return c.engine.ApprovePairingQR(ctx, r.Text, r.Passphrase)
	case "pairingGrants":
		return c.engine.PairingGrants(ctx)
	case "cancelGrant":
		return nil, c.engine.CancelGrant(ctx, r.DeviceID)
	case "cancelPairing":
		return nil, c.engine.CancelPairing(ctx)
	case "exportRecovery":
		text, err := c.vault.ExportRecovery(r.Passphrase)
		return core.Object{"recovery": text}, err
	case "validateRecovery":
		return c.engine.ValidateRecovery(r.Text)
	case "importRecovery":
		return c.engine.ImportRecovery(ctx, r.Text, r.Passphrase)
	case "status":
		return c.status(), nil
	case "create":
		if c.requireAccount {
			return nil, errors.New("请登录账户，由云端创建资料库")
		}
		recovery, err := c.vault.Create(r.Passphrase)
		if err != nil {
			return nil, err
		}
		return core.Object{"recovery": recovery}, nil
	case "unlock":
		if err := c.vault.Unlock(r.Passphrase, r.Recovery); err != nil {
			return nil, err
		}
		c.kick()
		return c.status(), nil
	case "lock":
		c.vault.Lock()
		return c.status(), nil
	case "list":
		clips, err := c.vault.List(r.Query, r.Kind, r.Group, r.Favourites)
		for _, clip := range clips {
			clip["localAvailable"], _ = c.vault.LocalAssetsAvailable(value(clip["id"]))
			sanitize(clip)
		}
		return clips, err
	case "addText":
		clip, err := c.vault.AddText(r.Text, r.HTML)
		if err == nil {
			c.kick()
		}
		return clip, err
	case "importShared":
		var bytes []byte
		var err error
		if r.Base64 != "" {
			bytes, err = base64.StdEncoding.DecodeString(r.Base64)
		}
		if err != nil {
			return nil, err
		}
		defer clear(bytes)
		result, err := c.vault.ImportShared(r.PacketID, r.Text, bytes, r.Name)
		if err == nil {
			c.kick()
		}
		return result, err
	case "importFile", "importFolder", "importBytes":
		var clip core.Object
		var err error
		if r.Action == "importFile" {
			clip, err = c.vault.ImportFile(r.Path)
		} else if r.Action == "importFolder" {
			clip, err = c.vault.ImportFolder(r.Path)
		} else {
			var b []byte
			b, err = base64.StdEncoding.DecodeString(r.Base64)
			if err == nil {
				clip, err = c.vault.ImportBytes(b, r.Name)
			}
			clear(b)
		}
		if err == nil {
			sanitize(clip)
			c.kick()
		}
		return clip, err
	case "favouriteAdd":
		err := c.vault.SetFavourite(r.ClipID, true)
		if err == nil {
			c.kick()
		}
		return nil, err
	case "favourite":
		err := c.vault.Favourite(r.ClipID)
		if err == nil {
			c.kick()
		}
		return nil, err
	case "group":
		var err error
		if r.ClipID == "" {
			err = c.vault.AddGroup(r.Name)
		} else {
			err = c.vault.SetGroup(r.ClipID, r.Name)
		}
		if err == nil {
			c.kick()
		}
		return nil, err
	case "delete":
		err := c.vault.Delete(r.ClipID)
		if err == nil {
			c.kick()
		}
		return nil, err
	case "thumbnail", "imageThumbnail":
		clip, err := c.vault.Find(r.ClipID)
		if err != nil {
			return nil, err
		}
		if value(clip["type"]) != "image" {
			return nil, errors.New("此内容没有本机图片缩略图")
		}
		// Only iOS opts into visible-image prefetch; other clients keep their
		// existing local-only thumbnail contract. Cached chunks stay encrypted.
		if r.Action == "imageThumbnail" {
			downloadCtx, cancel := context.WithTimeout(ctx, 8*time.Second)
			defer cancel()
			if err = c.engine.DownloadAssets(downloadCtx, r.ClipID); err != nil {
				return nil, err
			}
		}
		data, err := c.vault.ReadPreview(r.ClipID)
		if err != nil {
			return nil, err
		}
		result := core.Object{"bytes": base64.StdEncoding.EncodeToString(data), "type": clip["type"], "name": clip["name"]}
		clear(data)
		return result, nil
	case "preview":
		clip, err := c.vault.Find(r.ClipID)
		if err != nil {
			return nil, err
		}
		result := core.Object{"type": clip["type"], "name": clip["name"], "text": clip["text"], "html": clip["html"]}
		if lenParts(clip) > 0 {
			if err = c.engine.DownloadAssets(ctx, r.ClipID); err != nil {
				return nil, err
			}
			b, err := c.vault.ReadPreview(r.ClipID)
			if err != nil {
				return nil, err
			}
			result["bytes"] = base64.StdEncoding.EncodeToString(b)
			clear(b)
		}
		return result, nil
	case "exportLocal":
		// Drag representations must never trigger a network download. Export
		// authenticates/decrypts only existing local chunks and fails closed.
		return nil, c.vault.Export(r.ClipID, r.Path)
	case "export":
		if err := c.engine.DownloadAssets(ctx, r.ClipID); err != nil {
			return nil, err
		}
		return nil, c.vault.Export(r.ClipID, r.Path)
	case "config":
		if _, changing := r.Values["retentionDays"]; changing {
			st, err := c.vault.Snapshot()
			if err != nil {
				return nil, err
			}
			if access, ok := st.Config["membership"].(map[string]any); ok && access["tier"] == "free" {
				return nil, errors.New("pro_feature_required")
			}
		}
		if err := validateConfig(r.Values); err != nil {
			return nil, err
		}
		if err := c.vault.UpdateConfig(r.Values); err != nil {
			return nil, err
		}
		return c.status(), nil
	case "requestCode":
		return c.engine.RequestCode(ctx, r.Endpoint, r.Email)
	case "connect":
		recovery, err := c.engine.Connect(ctx, r.Endpoint, r.Email, r.Code, r.Passphrase, r.Recovery)
		result := core.Object{}
		if recovery != "" {
			result["recovery"] = recovery
		}
		return result, err
	case "sync":
		err := c.engine.Sync(ctx)
		return c.status(), err
	case "membership":
		return c.engine.Membership(ctx, false)
	case "refreshMembership":
		return c.engine.Membership(ctx, true)
	case "devices":
		return c.engine.Devices(ctx)
	case "events":
		return c.engine.Events(ctx)
	case "revoke":
		return nil, c.engine.Revoke(ctx, r.DeviceID, r.Passphrase)
	case "requestAccountDeletion":
		return c.engine.RequestAccountDeletion(ctx, r.Confirm)
	case "disconnect":
		return nil, c.engine.Disconnect(ctx)
	case "clearUnused":
		n, err := c.vault.ClearUnused()
		return core.Object{"removed": n}, err
	default:
		return nil, errors.New("不支持的客户端操作")
	}
}
func lenParts(c core.Object) int {
	switch v := c["parts"].(type) {
	case []any:
		return len(v)
	case []core.Object:
		return len(v)
	}
	return 0
}
func validateConfig(values core.Object) error {
	if values == nil {
		return errors.New("缺少设置值")
	}
	for k, v := range values {
		switch k {
		case "captureEnabled", "captureFiles", "directPaste":
			if _, ok := v.(bool); !ok {
				return errors.New("开关设置必须为布尔值")
			}
		case "retentionDays":
			n, err := strconvInt(v)
			if err != nil || (n != 0 && n != 7 && n != 30 && n != 90) {
				return errors.New("保留期限只能是 0、7、30 或 90 天")
			}
		case "excludedApps":
			if s, ok := v.(string); !ok || len(s) > 16384 {
				return errors.New("来源排除设置无效")
			}
		case "endpoint":
			if _, err := syncer.NewAPI(value(v)); err != nil {
				return err
			}
		case "email":
			if s, ok := v.(string); !ok || len(s) > 254 {
				return errors.New("邮箱设置无效")
			}
		default:
			return fmt.Errorf("不允许修改此设置：%s", k)
		}
	}
	return nil
}
func strconvInt(v any) (int64, error) {
	switch n := v.(type) {
	case json.Number:
		return n.Int64()
	case int:
		return int64(n), nil
	case int64:
		return n, nil
	}
	return 0, errors.New("必须是整数")
}
