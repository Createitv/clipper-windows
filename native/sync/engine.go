package syncer

import (
	"clipweave.local/device/core"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strconv"
	"strings"
	"sync"
	"time"
)

type Engine struct {
	deviceInfo  DeviceInfo
	pending     *pendingLogin
	vault       *core.Vault
	mu          sync.Mutex
	statusMu    sync.RWMutex
	status      string
	lastSuccess time.Time
}

func NewEngine(v *core.Vault) *Engine {
	return &Engine{vault: v, deviceInfo: defaultDeviceInfo(), status: "等待云端同步"}
}
func (e *Engine) Status() string { e.statusMu.RLock(); defer e.statusMu.RUnlock(); return e.status }
func (e *Engine) LastSuccess() int64 {
	e.statusMu.RLock()
	defer e.statusMu.RUnlock()
	if e.lastSuccess.IsZero() {
		return 0
	}
	return e.lastSuccess.UnixMilli()
}
func (e *Engine) setStatus(s string) { e.statusMu.Lock(); e.status = s; e.statusMu.Unlock() }
func value(x any) string             { s, _ := x.(string); return s }
func number(x any) int64 {
	switch n := x.(type) {
	case float64:
		return int64(n)
	case json.Number:
		v, _ := n.Int64()
		return v
	case int64:
		return n
	case int:
		return int64(n)
	}
	return 0
}
func parts(clip core.Object) []core.Object {
	out := []core.Object{}
	switch xs := clip["parts"].(type) {
	case []any:
		for _, x := range xs {
			if p, ok := x.(map[string]any); ok {
				out = append(out, p)
			}
		}
	case []core.Object:
		out = xs
	}
	return out
}
func sign(secret string, fields ...any) (string, error) {
	b, err := core.Marshal(fields)
	if err != nil {
		return "", err
	}
	return core.Sign(b, secret)
}
func verify(public, signature string, fields ...any) bool {
	b, err := core.Marshal(fields)
	return err == nil && core.Verify(b, signature, public)
}

type cloudVault struct {
	ID       string `json:"id"`
	RootKey  string `json:"root_key"`
	Envelope string `json:"envelope"`
	Epoch    int64  `json:"epoch"`
	Version  int64  `json:"version"`
}
type cloudDevice struct {
	ID          string `json:"id"`
	PublicKey   string `json:"public_key"`
	NameCipher  string `json:"name_cipher"`
	Certificate string `json:"certificate"`
	Revoked     int    `json:"revoked"`
	Created     int64  `json:"created"`
	Seen        int64  `json:"seen"`
}

func (e *Engine) RequestCode(ctx context.Context, endpoint, email string) (core.Object, error) {
	a, err := NewAPI(endpoint)
	if err != nil {
		return nil, err
	}
	var result core.Object
	err = a.call(ctx, "POST", "/auth/start", "", core.Object{"email": email}, &result)
	return result, err
}
func (e *Engine) Connect(ctx context.Context, endpoint, email, code, pass string, recovery bool) (string, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	a, err := NewAPI(endpoint)
	if err != nil {
		return "", err
	}
	if !e.vault.Unlocked() && e.vault.Exists() {
		if err = e.vault.Unlock(pass, recovery); err != nil {
			return "", err
		}
	}
	var login loginState
	if err = a.call(ctx, "POST", "/auth/verify", "", core.Object{"email": email, "code": code}, &login); err != nil {
		return "", err
	}
	if login.PasswordSetupRequired {
		return "", errors.New("邮箱已验证，请使用密码设置流程完成首次登录")
	}
	if login.Token == "" || login.AccountID == "" || login.Challenge == "" {
		return "", errors.New("服务器登录响应不完整")
	}
	if e.vault.Unlocked() {
		st, er := e.vault.Snapshot()
		if er != nil {
			return "", er
		}
		if old := value(st.Config["accountId"]); old != "" && old != login.AccountID {
			return "", errors.New("当前资料库已连接其他账户，请保留并导出后再切换")
		}
	}
	var remote struct {
		Vault *cloudVault `json:"vault"`
	}
	if err = a.call(ctx, "GET", "/vault", login.Token, nil, &remote); err != nil {
		return "", err
	}
	generated := ""
	if remote.Vault == nil {
		if !e.vault.Unlocked() {
			if recovery {
				return "", errors.New("新账户尚无资料库，请设置新解密口令")
			}
			generated, err = e.vault.Create(pass)
			if err != nil {
				return "", err
			}
		}
		env, er := e.vault.Envelope()
		if er != nil {
			return generated, er
		}
		sec, er := core.UnlockEnvelope(env, pass, recovery)
		if er != nil {
			return generated, er
		}
		raw, er := core.Marshal(env)
		if er != nil {
			return generated, er
		}
		proof, er := sign(sec.RootSecret, "create", login.AccountID, env.VaultID, env.RootPublic, string(raw), login.Challenge)
		sec.RootSecret = ""
		if er != nil {
			return generated, er
		}
		if err = a.call(ctx, "POST", "/vault", login.Token, core.Object{"vaultId": env.VaultID, "rootKey": env.RootPublic, "envelope": string(raw), "proof": proof}, nil); err != nil {
			return generated, err
		}
	} else {
		var env core.Envelope
		if err = json.Unmarshal([]byte(remote.Vault.Envelope), &env); err != nil {
			return "", errors.New("云资料库密钥包损坏")
		}
		if env.VaultID != remote.Vault.ID || env.RootPublic != remote.Vault.RootKey {
			return "", errors.New("云资料库身份不匹配")
		}
		if !e.vault.Unlocked() {
			err = e.vault.Join(env, pass, recovery)
		} else {
			local, _ := e.vault.Envelope()
			if local.VaultID != env.VaultID {
				err = e.vault.Join(env, pass, recovery)
			} else {
				_, err = core.UnlockEnvelope(env, pass, recovery)
			}
		}
		if err != nil {
			return "", err
		}
	}
	env, err := e.vault.Envelope()
	if err != nil {
		return generated, err
	}
	sec, err := core.UnlockEnvelope(env, pass, recovery)
	if err != nil {
		return generated, err
	}
	st, err := e.vault.Snapshot()
	if err != nil {
		return generated, err
	}
	proof, err := sign(sec.RootSecret, "enroll", login.AccountID, env.VaultID, st.DeviceID, st.DevicePublic, login.Challenge)
	sec.RootSecret = ""
	if err != nil {
		return generated, err
	}
	box, err := core.Seal(e.deviceNameBytes(), sec.Key, "device:"+st.DeviceID)
	if err != nil {
		return generated, err
	}
	raw, _ := core.Marshal(box)
	if err = a.call(ctx, "POST", "/devices/enroll", login.Token, core.Object{"id": st.DeviceID, "publicKey": st.DevicePublic, "nameCipher": string(raw), "proof": proof}, nil); err != nil {
		return generated, err
	}
	if err = e.vault.UpdateConfig(core.Object{"endpoint": endpoint, "email": email, "accountId": login.AccountID, "token": login.Token, "sessionExpires": login.Expires, "proofVersion": login.ProofVersion, "accountDeletionRequest": nil}); err != nil {
		return generated, err
	}
	err = e.sync(ctx)
	return generated, err
}
func (e *Engine) trusted(ctx context.Context, a *API, token, account string) (map[string]cloudDevice, error) {
	var result struct {
		Devices []cloudDevice `json:"devices"`
	}
	if err := a.call(ctx, "GET", "/devices", token, nil, &result); err != nil {
		return nil, err
	}
	env, err := e.vault.Envelope()
	if err != nil {
		return nil, err
	}
	trusted := map[string]cloudDevice{}
	for _, d := range result.Devices {
		var cert struct {
			Challenge string `json:"challenge"`
			Proof     string `json:"proof"`
		}
		if json.Unmarshal([]byte(d.Certificate), &cert) != nil || !verify(env.RootPublic, cert.Proof, "enroll", account, env.VaultID, d.ID, d.PublicKey, cert.Challenge) {
			return nil, errors.New("设备授权签名验证失败")
		}
		trusted[d.ID] = d
	}
	return trusted, nil
}
func (e *Engine) Sync(ctx context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.sync(ctx)
}
func (e *Engine) sync(ctx context.Context) (result error) {
	if !e.vault.Unlocked() {
		e.setStatus("资料库已锁定")
		return nil
	}
	if err := e.vault.EnsureGroupsSynced(); err != nil {
		return err
	}
	st, err := e.vault.Snapshot()
	if err != nil {
		return err
	}
	token := value(st.Config["token"])
	if token == "" {
		e.setStatus("请登录并连接云端")
		return errors.New("请先登录并连接云端")
	}
	e.setStatus("正在同步")
	defer func() {
		if result != nil {
			var cloud *CloudError
			if errors.As(result, &cloud) && (cloud.Status == 401 || cloud.Status == 403) {
				if cloud.Code == "unauthorized" || cloud.Code == "device_revoked" || cloud.Code == "login_required" {
					_ = e.vault.UpdateConfig(core.Object{"token": nil})
				}
				e.setStatus("授权失效，请重新连接账户")
			} else {
				e.setStatus("同步失败，待上传内容已暂存，将自动重试")
			}
		}
	}()
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return err
	}
	a.signingSecret = st.DeviceSecret
	if number(st.Config["proofVersion"]) == 1 && time.Now().UnixMilli()-number(st.Config["sessionChecked"]) > 300000 {
		var me struct {
			AccountID string `json:"accountId"`
			Email     string `json:"email"`
			Name      string `json:"name"`
			Expires   int64  `json:"expires"`
		}
		if err = a.call(ctx, "GET", "/auth/me", token, nil, &me); err != nil {
			return err
		}
		if me.AccountID != value(st.Config["accountId"]) || me.Email == "" || me.Expires <= time.Now().UnixMilli() {
			_ = e.vault.UpdateConfig(core.Object{"token": nil})
			return errors.New("会话账户信息与本机不一致，请重新登录")
		}
		if err = e.vault.UpdateConfig(core.Object{"email": me.Email, "accountName": me.Name, "sessionExpires": me.Expires, "sessionChecked": time.Now().UnixMilli()}); err != nil {
			return err
		}
		st.Config["sessionExpires"] = me.Expires
	}
	if number(st.Config["proofVersion"]) == 1 && number(st.Config["sessionExpires"]) > 0 && number(st.Config["sessionExpires"])-time.Now().UnixMilli() < 86400000 {
		var fresh struct {
			Token   string `json:"token"`
			Expires int64  `json:"expires"`
		}
		if er := a.call(ctx, "POST", "/auth/refresh", token, core.Object{}, &fresh); er != nil {
			return er
		}
		if fresh.Token == "" || fresh.Expires <= time.Now().UnixMilli() {
			return errors.New("会话刷新响应无效")
		}
		if er := e.vault.UpdateConfig(core.Object{"token": fresh.Token, "sessionExpires": fresh.Expires}); er != nil {
			return er
		}
		token = fresh.Token
	}
	account := value(st.Config["accountId"])
	env, err := e.vault.Envelope()
	if err != nil {
		return err
	}
	sec, err := e.vault.Credentials()
	if err != nil {
		return err
	}
	var remote struct {
		Vault *cloudVault `json:"vault"`
	}
	if err = a.call(ctx, "GET", "/vault", token, nil, &remote); err != nil {
		return err
	}
	if remote.Vault == nil || remote.Vault.ID != env.VaultID || remote.Vault.RootKey != env.RootPublic || remote.Vault.Epoch != sec.Epoch {
		_ = e.vault.UpdateConfig(core.Object{"cloudSyncedAt": nil})
		return errors.New("云端资料库身份发生变化，请重新登录并授权")
	}
	var access core.Object
	if err = a.call(ctx, "GET", "/billing/entitlement", token, nil, &access); err != nil {
		return err
	}
	if access["unavailable"] == true {
		return errors.New("membership_unavailable")
	}
	if err = e.vault.UpdateConfig(core.Object{"membership": access}); err != nil {
		return err
	}
	trusted, err := e.trusted(ctx, a, token, account)
	if err != nil {
		return err
	}
	failures := 0
	var firstFailure error
	for _, pending := range st.Outbox {
		if err = e.uploadOperation(ctx, a, token, account, env.VaultID, st.DeviceSecret, pending); err != nil {
			failures++
			if firstFailure == nil {
				firstFailure = err
			}
		}
	}
	pages := 0
	cursor := st.Cursor
	for {
		pages++
		if pages > 10000 {
			return errors.New("历史分页超过限制，请重新同步")
		}
		var response struct {
			Operations []struct {
				Cursor int64       `json:"cursor"`
				Packet core.Packet `json:"packet"`
			} `json:"operations"`
		}
		if err = a.call(ctx, "GET", "/operations?cursor="+strconv.FormatInt(cursor, 10), token, nil, &response); err != nil {
			return err
		}
		for _, row := range response.Operations {
			p := row.Packet
			d, ok := trusted[p.DeviceID]
			if !ok || p.Epoch != sec.Epoch || !verify(d.PublicKey, p.Signature, "operation", account, env.VaultID, p.ID, p.DeviceID, p.Epoch, p.Sequence, p.Cipher, p.AssetIDs) {
				return errors.New("历史记录签名或密钥时期不正确")
			}
			if row.Cursor <= cursor {
				return errors.New("同步游标未前进")
			}
			op, er := e.vault.DecodeOperation(p)
			if er != nil {
				return er
			}
			expected := []string{}
			for _, part := range parts(op.Clip) {
				expected = append(expected, value(part["id"]))
			}
			if !reflect.DeepEqual(expected, p.AssetIDs) {
				return errors.New("附件清单与签名不一致")
			}
			if er = e.vault.ApplyRemote(op, row.Cursor); er != nil {
				return er
			}
			cursor = row.Cursor
		}
		if len(response.Operations) < 100 {
			break
		}
	}
	if failures > 0 {
		return fmt.Errorf("已拉取远端历史；%d 条上传待重试：%w", failures, firstFailure)
	}
	remaining, err := e.vault.Pending()
	if err != nil {
		return err
	}
	if len(remaining) > 0 {
		e.setStatus(fmt.Sprintf("%d 条待上传", len(remaining)))
	} else {
		if err := e.vault.UpdateConfig(core.Object{"cloudSyncedAt": time.Now().UnixMilli()}); err != nil {
			return err
		}
		e.publishDeviceInfo(ctx, a, token)
		e.setStatus("历史已保存到云端；附件按需缓存")
		e.statusMu.Lock()
		e.lastSuccess = time.Now()
		e.statusMu.Unlock()
	}
	return nil
}
func (e *Engine) uploadOperation(ctx context.Context, a *API, token, account, vault, deviceSecret string, p core.Packet) error {
	op, err := e.vault.DecodeOperation(p)
	if err != nil {
		return err
	}
	ids := []string{}
	for _, part := range parts(op.Clip) {
		id := value(part["id"])
		path, er := e.vault.AssetPath(id)
		if er != nil {
			return er
		}
		size := number(part["size"])
		if size < 40 || size > 5*1024*1024 {
			return errors.New("附件大小无效")
		}
		var init struct {
			State string `json:"state"`
		}
		if er = a.call(ctx, "POST", "/assets/init", token, core.Object{"id": id, "size": size, "digest": part["digest"]}, &init); er != nil {
			return er
		}
		if init.State != "ready" {
			data, er := os.ReadFile(path)
			if er != nil {
				return er
			}
			if int64(len(data)) != size || digest(data) != value(part["digest"]) {
				return errors.New("本地附件完整性验证失败")
			}
			if er = a.upload(ctx, id, token, data); er != nil {
				return er
			}
		}
		ids = append(ids, id)
	}
	p.AssetIDs = ids
	p.Signature, err = sign(deviceSecret, "operation", account, vault, p.ID, p.DeviceID, p.Epoch, p.Sequence, p.Cipher, ids)
	if err != nil {
		return err
	}
	if err = a.call(ctx, "POST", "/operations", token, p, nil); err != nil {
		return err
	}
	return e.vault.Acknowledge(p.ID)
}
func digest(b []byte) string { d := sha256.Sum256(b); return hex.EncodeToString(d[:]) }
func (e *Engine) DownloadAssets(ctx context.Context, id string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	clip, err := e.vault.Find(id)
	if err != nil {
		return err
	}
	st, err := e.vault.Snapshot()
	if err != nil {
		return err
	}
	for _, p := range parts(clip) {
		path, er := e.vault.AssetPath(value(p["id"]))
		if er != nil {
			return er
		}
		size := number(p["size"])
		if size < 40 || size > 5*1024*1024 {
			return errors.New("附件大小无效")
		}
		if b, er := os.ReadFile(path); er == nil && int64(len(b)) == size && digest(b) == value(p["digest"]) {
			continue
		}
		a, er := NewAPI(value(st.Config["endpoint"]))
		if er != nil {
			return errors.New("附件未在本机且尚未连接云端")
		}
		a.signingSecret = st.DeviceSecret
		b, er := a.download(ctx, value(p["id"]), value(st.Config["token"]), size)
		if er != nil {
			return er
		}
		if int64(len(b)) != size || digest(b) != value(p["digest"]) {
			return errors.New("下载附件完整性验证失败")
		}
		if er = os.MkdirAll(filepath.Dir(path), 0700); er != nil {
			return er
		}
		f, er := os.CreateTemp(filepath.Dir(path), ".download-*")
		if er != nil {
			return er
		}
		tmp := f.Name()
		_ = f.Chmod(0600)
		_, er = f.Write(b)
		if er == nil {
			er = f.Sync()
		}
		ce := f.Close()
		if er == nil {
			er = ce
		}
		if er == nil {
			er = os.Rename(tmp, path)
		}
		_ = os.Remove(tmp)
		if er != nil {
			return er
		}
	}
	return nil
}
func (e *Engine) Devices(ctx context.Context) ([]core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	st, err := e.vault.Snapshot()
	if err != nil {
		return nil, err
	}
	token := value(st.Config["token"])
	if token == "" {
		return []core.Object{}, nil
	}
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return nil, err
	}
	a.signingSecret = st.DeviceSecret
	ds, err := e.trusted(ctx, a, token, value(st.Config["accountId"]))
	if err != nil {
		return nil, err
	}
	sec, err := e.vault.Credentials()
	if err != nil {
		return nil, err
	}
	out := []core.Object{}
	for _, d := range ds {
		var box core.Box
		if json.Unmarshal([]byte(d.NameCipher), &box) != nil {
			return nil, errors.New("设备名称密文损坏")
		}
		name, er := core.Open(box, sec.Key, "device:"+d.ID)
		if er != nil {
			return nil, er
		}
		info := decodeDeviceInfo(name)
		out = append(out, core.Object{"id": d.ID, "name": info.Name, "model": info.Model, "platform": info.Platform, "osVersion": info.OSVersion, "revoked": d.Revoked != 0, "created": d.Created, "seen": d.Seen, "current": d.ID == st.DeviceID})
	}
	return out, nil
}

// Membership is read only after the server verifies this enrolled device session.
// The UI never supplies an account ID or a paid flag.
func (e *Engine) Membership(ctx context.Context, force bool) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	st, err := e.vault.Snapshot()
	if err != nil {
		return nil, err
	}
	token, account := value(st.Config["token"]), value(st.Config["accountId"])
	if token == "" || account == "" || st.DeviceSecret == "" {
		return nil, errors.New("请先连接 Clipper 账户")
	}
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return nil, err
	}
	a.signingSecret = st.DeviceSecret
	var access core.Object
	if force {
		err = a.call(ctx, "POST", "/billing/refresh", token, core.Object{}, &access)
	} else {
		err = a.call(ctx, "GET", "/billing/entitlement", token, nil, &access)
	}
	if err != nil {
		return nil, err
	}
	if access["unavailable"] != true {
		if err = e.vault.UpdateConfig(core.Object{"membership": access}); err != nil {
			return nil, err
		}
	}
	return core.Object{"accountId": account, "entitlement": access}, nil
}
func (e *Engine) Events(ctx context.Context) ([]core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	st, err := e.vault.Snapshot()
	if err != nil {
		return nil, err
	}
	if value(st.Config["token"]) == "" {
		return []core.Object{}, nil
	}
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return nil, err
	}
	a.signingSecret = st.DeviceSecret
	var response struct {
		Events []core.Object `json:"events"`
	}
	err = a.call(ctx, "GET", "/events", value(st.Config["token"]), nil, &response)
	return response.Events, err
}
func (e *Engine) Revoke(ctx context.Context, id, pass string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	st, err := e.vault.Snapshot()
	if err != nil {
		return err
	}
	if id == st.DeviceID {
		return errors.New("当前设备请使用退出云同步")
	}
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return err
	}
	a.signingSecret = st.DeviceSecret
	token := value(st.Config["token"])
	env, err := e.vault.Envelope()
	if err != nil {
		return err
	}
	var remote struct {
		Vault *cloudVault `json:"vault"`
	}
	if err = a.call(ctx, "GET", "/vault", token, nil, &remote); err != nil {
		return err
	}
	if remote.Vault == nil || remote.Vault.ID != env.VaultID || remote.Vault.RootKey != env.RootPublic {
		return errors.New("资料库身份变化")
	}
	sec, err := core.UnlockEnvelope(env, pass, false)
	if err != nil {
		return err
	}
	proof, err := sign(sec.RootSecret, "revoke", value(st.Config["accountId"]), env.VaultID, id, remote.Vault.Version)
	sec.RootSecret = ""
	if err != nil {
		return err
	}
	return a.call(ctx, "POST", "/devices/revoke", token, core.Object{"deviceId": id, "proof": proof}, nil)
}

// RequestAccountDeletion records a confirmed cloud request, then disconnects
// locally. It never equates server acceptance with completed data erasure.
func (e *Engine) RequestAccountDeletion(ctx context.Context, confirmed bool) (core.Object, error) {
	if !confirmed {
		return nil, errors.New("请先确认账户删除及订阅说明")
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	st, err := e.vault.Snapshot()
	if err != nil {
		return nil, err
	}
	token := value(st.Config["token"])
	if token == "" || value(st.Config["accountId"]) == "" || st.DeviceSecret == "" {
		return nil, errors.New("请先连接 Clipper 账户")
	}
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return nil, err
	}
	a.signingSecret = st.DeviceSecret
	var receipt struct {
		RequestID string `json:"requestId"`
		State     string `json:"state"`
		Created   int64  `json:"created"`
	}
	if err = a.call(ctx, "POST", "/account/deletion", token, core.Object{"confirm": true}, &receipt); err != nil {
		return nil, err
	}
	if receipt.RequestID == "" || len(receipt.RequestID) > 80 || strings.IndexFunc(receipt.RequestID, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || r == '-' || r == '_')
	}) >= 0 || (receipt.State != "requested" && receipt.State != "processing" && receipt.State != "completed") || receipt.Created <= 0 {
		return nil, errors.New("服务器未返回有效的删除请求凭据")
	}
	result := core.Object{"requestId": receipt.RequestID, "state": receipt.State, "created": receipt.Created}
	if err = e.vault.UpdateConfig(core.Object{"token": nil, "accountId": nil, "email": nil, "sessionExpires": nil, "accountDeletionRequest": result}); err != nil {
		return nil, errors.New("删除请求已登记，但本机状态未保存；请断开同步连接")
	}
	e.setStatus("删除请求已登记；本机已断开同步")
	return result, nil
}

func (e *Engine) Disconnect(ctx context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	st, err := e.vault.Snapshot()
	if err != nil {
		return err
	}
	// Local disconnection must succeed even if the old session is expired/offline.
	if err = e.vault.UpdateConfig(core.Object{"token": nil}); err != nil {
		return err
	}
	e.setStatus("已退出云同步；本机历史保留")
	token := value(st.Config["token"])
	if token == "" {
		return nil
	}
	a, remoteErr := NewAPI(value(st.Config["endpoint"]))
	if remoteErr == nil {
		a.signingSecret = st.DeviceSecret
		remoteErr = a.call(ctx, "POST", "/logout", token, core.Object{}, nil)
	}
	if remoteErr != nil {
		var cloud *CloudError
		if !errors.As(remoteErr, &cloud) || (cloud.Status != 401 && cloud.Status != 403) {
			e.setStatus("已在本机断开；服务端会话未确认注销")
		}
	}
	return nil
}
