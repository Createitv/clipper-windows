package syncer

import (
	"clipweave.local/device/core"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/url"
	"strings"
	"time"
)

type PairRequest struct {
	DeviceInfo  DeviceInfo `json:"deviceInfo"`
	Created     int64      `json:"created"`
	ApprovedAt  int64      `json:"approvedAt,omitempty"`
	ID          string     `json:"id"`
	Code        string     `json:"code"`
	Expires     int64      `json:"expires"`
	PublicKey   string     `json:"publicKey"`
	Kind        string     `json:"kind"`
	Status      string     `json:"status"`
	AccountID   string     `json:"accountId"`
	VaultID     string     `json:"vaultId"`
	Payload     string     `json:"payload,omitempty"`
	Fingerprint string     `json:"fingerprint,omitempty"`
}
type loginState struct {
	PasswordSetupRequired bool   `json:"passwordSetupRequired"`
	VerificationToken     string `json:"verificationToken"`
	Token                 string `json:"token"`
	AccountID             string `json:"accountId"`
	Challenge             string `json:"challenge"`
	Expires               int64  `json:"expires"`
	ProofVersion          int    `json:"proofVersion"`
}
type pendingLogin struct {
	remembered bool // Opt in only when the platform securely checkpoints this session.
	loginState
	Endpoint string
	Email    string
	Remote   *cloudVault
	Pair     PairRequest
	Private  *rsa.PrivateKey
}
type pairBox struct {
	Key    string `json:"key"`
	Nonce  string `json:"nonce"`
	Cipher string `json:"cipher"`
}

func fingerprint(public string) string {
	raw, _ := base64.StdEncoding.DecodeString(public)
	h := sha256.Sum256(raw)
	return strings.ToUpper(hex.EncodeToString(h[:8]))
}
func sealPair(public, id string, payload any) (string, error) {
	raw, err := base64.StdEncoding.DecodeString(public)
	if err != nil {
		return "", err
	}
	parsed, err := x509.ParsePKIXPublicKey(raw)
	if err != nil {
		return "", err
	}
	pub, ok := parsed.(*rsa.PublicKey)
	if !ok || pub.N.BitLen() != 2048 || pub.E != 65537 {
		return "", errors.New("配对公钥无效")
	}
	key := make([]byte, 32)
	if _, err = rand.Read(key); err != nil {
		return "", err
	}
	defer clear(key)
	wrapped, err := rsa.EncryptOAEP(sha256.New(), rand.Reader, pub, key, nil)
	if err != nil {
		return "", err
	}
	block, _ := aes.NewCipher(key)
	gcm, _ := cipher.NewGCM(block)
	nonce := make([]byte, gcm.NonceSize())
	if _, err = rand.Read(nonce); err != nil {
		return "", err
	}
	plain, err := core.Marshal(payload)
	if err != nil {
		return "", err
	}
	defer clear(plain)
	box := pairBox{base64.StdEncoding.EncodeToString(wrapped), base64.StdEncoding.EncodeToString(nonce), base64.StdEncoding.EncodeToString(gcm.Seal(nil, nonce, plain, []byte("cliper-pair-v1:"+id)))}
	data, err := core.Marshal(box)
	return string(data), err
}
func openPair(private *rsa.PrivateKey, id, payload string) (core.Access, error) {
	var box pairBox
	var access core.Access
	if len(payload) > 65536 || json.Unmarshal([]byte(payload), &box) != nil {
		return access, errors.New("配对响应无效")
	}
	wrapped, err := base64.StdEncoding.DecodeString(box.Key)
	if err != nil {
		return access, err
	}
	key, err := rsa.DecryptOAEP(sha256.New(), rand.Reader, private, wrapped, nil)
	if err != nil {
		return access, err
	}
	defer clear(key)
	block, err := aes.NewCipher(key)
	if err != nil {
		return access, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return access, err
	}
	nonce, err := base64.StdEncoding.DecodeString(box.Nonce)
	if err != nil || len(nonce) != gcm.NonceSize() {
		return access, errors.New("配对随机数无效")
	}
	encrypted, err := base64.StdEncoding.DecodeString(box.Cipher)
	if err != nil {
		return access, err
	}
	plain, err := gcm.Open(nil, nonce, encrypted, []byte("cliper-pair-v1:"+id))
	if err != nil {
		return access, err
	}
	defer clear(plain)
	err = json.Unmarshal(plain, &access)
	return access, err
}
func (e *Engine) AutoConnect(ctx context.Context, endpoint, email, code, pass string) (core.Object, error) {
	return e.autoConnect(ctx, endpoint, email, code, "", pass)
}
func (e *Engine) PasswordConnect(ctx context.Context, endpoint, email, code, password, pass string) (core.Object, error) {
	return e.autoConnect(ctx, endpoint, email, code, password, pass)
}
func (e *Engine) autoConnect(ctx context.Context, endpoint, email, code, password, pass string) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	a, err := NewAPI(endpoint)
	if err != nil {
		return nil, err
	}
	var login loginState
	path := "/auth/verify"
	credentials := core.Object{"email": email, "code": code}
	if password != "" {
		credentials["password"] = password
		if code == "" {
			path = "/auth/password"
		}
	}
	if err = a.call(ctx, "POST", path, "", credentials, &login); err != nil {
		return nil, err
	}
	if login.PasswordSetupRequired {
		return core.Object{"passwordSetupRequired": true, "verificationToken": login.VerificationToken}, nil
	}
	if login.Token == "" || login.AccountID == "" || login.Challenge == "" {
		return nil, errors.New("服务器登录响应不完整")
	}
	p := &pendingLogin{loginState: login, Endpoint: endpoint, Email: email}
	e.pending = p
	return e.continueConnect(ctx, p, pass)
}
func (e *Engine) continueConnect(ctx context.Context, p *pendingLogin, pass string) (core.Object, error) {
	login := p.loginState
	a, err := NewAPI(p.Endpoint)
	if err != nil {
		return nil, err
	}
	var result struct {
		Vault *cloudVault `json:"vault"`
	}
	if err = a.call(ctx, "GET", "/vault", login.Token, nil, &result); err != nil {
		return nil, err
	}
	p.Remote = result.Vault
	// Authenticate before touching cache selection. Local files are not the
	// authority for which cloud library belongs to the signed-in account.
	if e.vault.Exists() && !e.vault.Unlocked() {
		_ = e.vault.Unlock(pass, false)
	}
	mismatch := e.vault.Exists() && !e.vault.Unlocked()
	if e.vault.Unlocked() {
		st, _ := e.vault.Snapshot()
		env, _ := e.vault.Envelope()
		old := value(st.Config["accountId"])
		mismatch = (old != "" && old != login.AccountID) || (p.Remote != nil && (env.VaultID != p.Remote.ID || env.RootPublic != p.Remote.RootKey))
	}
	if mismatch {
		remoteID := ""
		if p.Remote != nil {
			remoteID = p.Remote.ID
		}
		if err = e.vault.SelectCloudCache(login.AccountID, remoteID, pass); err != nil {
			return nil, err
		}
	}
	if p.Remote == nil {
		if !e.vault.Exists() {
			if _, err = e.vault.Create(pass); err != nil {
				return nil, err
			}
			if err = e.vault.UpdateConfig(core.Object{"autoProvisional": true, "accountId": login.AccountID}); err != nil {
				return nil, err
			}
		}
		access, er := e.vault.Access(pass)
		if er != nil {
			return nil, er
		}
		raw, _ := core.Marshal(access.Envelope)
		proof, er := sign(access.Secrets.RootSecret, "create", login.AccountID, access.Envelope.VaultID, access.Envelope.RootPublic, string(raw), login.Challenge)
		if er != nil {
			return nil, er
		}
		if err = a.call(ctx, "POST", "/vault", login.Token, core.Object{"vaultId": access.Envelope.VaultID, "rootKey": access.Envelope.RootPublic, "envelope": string(raw), "proof": proof}, nil); err != nil {
			return nil, err
		}
		p.Remote = &cloudVault{ID: access.Envelope.VaultID, RootKey: access.Envelope.RootPublic, Epoch: access.Secrets.Epoch, Envelope: string(raw)}
		recovery, er := e.vault.ExportRecovery(pass)
		if er != nil {
			return nil, er
		}
		out, er := e.finishConnect(ctx, p, pass)
		if out == nil {
			out = core.Object{}
		}
		out["recovery"] = recovery
		return out, er
	}
	if e.vault.Unlocked() {
		env, _ := e.vault.Envelope()
		if env.VaultID == p.Remote.ID && env.RootPublic == p.Remote.RootKey {
			return e.finishConnect(ctx, p, pass)
		}
		if !e.vault.ReplaceableProvisional() {
			return nil, errors.New("账户缓存与云端身份不匹配，原资料库已保留，请使用恢复文件连接云端")
		}
	}
	return e.startPairing(ctx, p)
}
func (e *Engine) startPairing(ctx context.Context, p *pendingLogin) (core.Object, error) {
	a, err := NewAPI(p.Endpoint)
	if err != nil {
		return nil, err
	}
	private, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return nil, err
	}
	raw, err := x509.MarshalPKIXPublicKey(&private.PublicKey)
	if err != nil {
		return nil, err
	}
	public := base64.StdEncoding.EncodeToString(raw)
	err = a.call(ctx, "POST", "/pairing/start", p.Token, core.Object{"publicKey": public, "kind": "native", "deviceInfo": e.deviceInfo}, &p.Pair)
	var cloud *CloudError
	// Additive rollout: an older strict server can still authorize this device.
	if errors.As(err, &cloud) && cloud.Status == 400 {
		err = a.call(ctx, "POST", "/pairing/start", p.Token, core.Object{"publicKey": public, "kind": "native"}, &p.Pair)
	}
	if err != nil {
		return nil, err
	}
	if p.Pair.DeviceInfo.Name == "" {
		p.Pair.DeviceInfo = e.deviceInfo
	}
	if p.Pair.Created == 0 {
		p.Pair.Created = time.Now().UnixMilli()
	}
	p.Private = private
	p.Pair.PublicKey = public
	p.Pair.Fingerprint = fingerprint(public)
	return core.Object{"state": "pairing", "pairing": p.Pair}, nil
}
func (e *Engine) finishConnect(ctx context.Context, p *pendingLogin, pass string) (core.Object, error) {
	access, err := e.vault.Access(pass)
	if err != nil {
		return nil, err
	}
	if p.Remote == nil || access.Envelope.VaultID != p.Remote.ID || access.Envelope.RootPublic != p.Remote.RootKey || access.Secrets.Epoch != p.Remote.Epoch {
		return nil, errors.New("资料库身份或密钥版本不匹配")
	}
	st, err := e.vault.Snapshot()
	if err != nil {
		return nil, err
	}
	proof, err := sign(access.Secrets.RootSecret, "enroll", p.AccountID, access.Envelope.VaultID, st.DeviceID, st.DevicePublic, p.Challenge)
	if err != nil {
		return nil, err
	}
	box, err := core.Seal(e.deviceNameBytes(), access.Secrets.Key, "device:"+st.DeviceID)
	if err != nil {
		return nil, err
	}
	raw, _ := core.Marshal(box)
	a, err := NewAPI(p.Endpoint)
	if err != nil {
		return nil, err
	}
	if err = a.call(ctx, "POST", "/devices/enroll", p.Token, core.Object{"id": st.DeviceID, "publicKey": st.DevicePublic, "nameCipher": string(raw), "proof": proof}, nil); err != nil {
		return nil, err
	}
	if err = e.vault.UpdateConfig(core.Object{"endpoint": p.Endpoint, "email": p.Email, "accountId": p.AccountID, "token": p.Token, "sessionExpires": p.Expires, "proofVersion": p.ProofVersion, "autoProvisional": false, "cloudSyncedAt": nil}); err != nil {
		return nil, err
	}
	e.pending = nil
	// Enrollment alone does not open the library; a complete cloud sync is required.
	err = e.sync(ctx)
	out := core.Object{"state": "connected"}
	if err != nil {
		out["state"] = "syncRequired"
		out["syncNotice"] = err.Error()
	}
	return out, nil
}
func (e *Engine) CompletePairing(ctx context.Context, pass string) (out core.Object, err error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	defer func() {
		var cloud *CloudError
		if e.pending != nil && e.pending.remembered && errors.As(err, &cloud) && (cloud.Status == 401 || cloud.Code == "login_required" || cloud.Code == "device_revoked") {
			e.pending = nil
		}
	}()
	p := e.pending
	if p == nil {
		return nil, errors.New("请先登录并生成授权码")
	}
	if p.remembered && p.Expires <= time.Now().UnixMilli() {
		e.pending = nil
		return nil, errors.New("账号会话已过期，请重新登录")
	}
	if p.Remote == nil || p.Private == nil {
		if !p.remembered {
			return nil, errors.New("请先登录并生成授权码")
		}
		return e.continueConnect(ctx, p, pass)
	}
	if p.Pair.Expires <= time.Now().UnixMilli() {
		if !p.remembered {
			return nil, errors.New("授权码已过期，请重新登录生成")
		}
		return e.startPairing(ctx, p)
	}
	a, err := NewAPI(p.Endpoint)
	if err != nil {
		return nil, err
	}
	var result PairRequest
	if err = a.call(ctx, "GET", "/pairing/result?id="+url.QueryEscape(p.Pair.ID), p.Token, nil, &result); err != nil {
		return nil, err
	}
	if p.remembered && (result.Status == "cancelled" || result.Status == "canceled" || result.Status == "expired") {
		return e.startPairing(ctx, p)
	}
	if result.Status != "approved" {
		return core.Object{"state": "pairing", "pairing": p.Pair}, nil
	}
	access, err := openPair(p.Private, p.Pair.ID, result.Payload)
	if err != nil {
		return nil, err
	}
	if access.Envelope.VaultID != p.Remote.ID || access.Envelope.RootPublic != p.Remote.RootKey || access.Secrets.Epoch != p.Remote.Epoch {
		return nil, errors.New("授权材料与账户资料库不匹配")
	}
	if err = e.vault.InstallAccess(access, pass); err != nil {
		return nil, err
	}
	return e.finishConnect(ctx, p, pass)
}
func (e *Engine) ImportRecovery(ctx context.Context, text, pass string) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	access, err := core.ReadRecovery(text)
	if err != nil {
		return nil, err
	}
	p := e.pending
	if p != nil && (p.Remote == nil || p.Remote.ID != access.Envelope.VaultID || p.Remote.RootKey != access.Envelope.RootPublic) {
		return nil, errors.New("恢复文件不属于当前账户资料库")
	}
	// Locked existing same vault can be unlocked with the portable recovery envelope's secrets.
	if e.vault.Exists() && !e.vault.Unlocked() {
		if err = e.vault.UnlockAccess(access); err != nil {
			return nil, err
		}
	}
	if err = e.vault.InstallAccess(access, pass); err != nil {
		return nil, err
	}
	if p != nil {
		return e.finishConnect(ctx, p, pass)
	}
	return core.Object{"state": "connected"}, nil
}
func (e *Engine) pairingAPI() (*API, string, error) {
	st, err := e.vault.Snapshot()
	if err != nil {
		return nil, "", err
	}
	token := value(st.Config["token"])
	if token == "" {
		return nil, "", errors.New("请先连接账户")
	}
	a, err := NewAPI(value(st.Config["endpoint"]))
	if err != nil {
		return nil, "", err
	}
	a.signingSecret = st.DeviceSecret
	return a, token, nil
}
func (e *Engine) InspectPairing(ctx context.Context, code string) (PairRequest, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.inspectPairing(ctx, code)
}
func (e *Engine) inspectPairing(ctx context.Context, code string) (PairRequest, error) {
	var p PairRequest
	a, token, err := e.pairingAPI()
	if err != nil {
		return p, err
	}
	code = strings.ToUpper(strings.ReplaceAll(strings.TrimSpace(code), "-", ""))
	if len(code) < 8 || len(code) > 32 {
		return p, errors.New("请输入完整授权码")
	}
	err = a.call(ctx, "GET", "/pairing/lookup?code="+url.QueryEscape(code), token, nil, &p)
	p.Fingerprint = fingerprint(p.PublicKey)
	return p, err
}
func (e *Engine) ApprovePairing(ctx context.Context, code, pass, expected string) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	p, err := e.inspectPairing(ctx, code)
	if err != nil {
		return nil, err
	}
	if expected == "" || p.Fingerprint != expected {
		return nil, errors.New("请先查看并核对新设备校验码")
	}
	if p.Status != "pending" || p.Expires <= time.Now().UnixMilli() {
		return nil, errors.New("授权请求已失效")
	}
	access, err := e.vault.Access(pass)
	if err != nil {
		return nil, err
	}
	st, _ := e.vault.Snapshot()
	if p.AccountID != value(st.Config["accountId"]) || p.VaultID != access.Envelope.VaultID {
		return nil, errors.New("授权请求不属于当前资料库")
	}
	var payload any = access
	if p.Kind == "web" {
		payload = core.Object{"envelope": core.Object{"vaultId": access.Envelope.VaultID, "rootPublic": access.Envelope.RootPublic}, "key": access.Secrets.Key, "epoch": access.Secrets.Epoch}
	} else if p.Kind != "native" {
		return nil, errors.New("不支持的设备类型")
	}
	encrypted, err := sealPair(p.PublicKey, p.ID, payload)
	if err != nil {
		return nil, err
	}
	a, token, err := e.pairingAPI()
	if err != nil {
		return nil, err
	}
	err = a.call(ctx, "POST", "/pairing/approve", token, core.Object{"id": p.ID, "payload": encrypted}, nil)
	return core.Object{"approved": err == nil}, err
}
func (e *Engine) CancelPairing(ctx context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	p := e.pending
	e.pending = nil
	if p == nil || p.Pair.ID == "" {
		return nil
	}
	a, err := NewAPI(p.Endpoint)
	if err != nil {
		return err
	}
	return a.call(ctx, "POST", "/pairing/cancel", p.Token, core.Object{"id": p.Pair.ID}, nil)
}
func (e *Engine) RenewPairing(ctx context.Context) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	p := e.pending
	if p == nil {
		return nil, errors.New("请先登录")
	}
	if p.Expires <= time.Now().UnixMilli() {
		e.pending = nil
		return nil, errors.New("账号会话已过期，请重新登录")
	}
	if p.Pair.ID != "" {
		a, err := NewAPI(p.Endpoint)
		if err != nil {
			return nil, err
		}
		if err = a.call(ctx, "POST", "/pairing/cancel", p.Token, core.Object{"id": p.Pair.ID}, nil); err != nil {
			return nil, err
		}
	}
	p.Pair = PairRequest{}
	if p.Remote == nil {
		return core.Object{"state": "pairing", "pairing": p.Pair}, nil
	}
	return e.startPairing(ctx, p)
}

func (e *Engine) PairingGrants(ctx context.Context) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	a, token, err := e.pairingAPI()
	if err != nil {
		return nil, err
	}
	var result core.Object
	err = a.call(ctx, "GET", "/pairing/list", token, nil, &result)
	return result, err
}
func (e *Engine) CancelGrant(ctx context.Context, id string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	a, token, err := e.pairingAPI()
	if err != nil {
		return err
	}
	return a.call(ctx, "POST", "/pairing/cancel", token, core.Object{"id": id}, nil)
}

func (e *Engine) ValidateRecovery(text string) (core.Object, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	access, err := core.ReadRecovery(text)
	if err != nil {
		return nil, err
	}
	if p := e.pending; p != nil && (p.Remote == nil || p.Remote.ID != access.Envelope.VaultID || p.Remote.RootKey != access.Envelope.RootPublic) {
		return nil, errors.New("恢复文件不属于当前账户资料库")
	}
	if err = e.vault.CheckRecovery(access); err != nil {
		return nil, err
	}
	return core.Object{"vaultId": access.Envelope.VaultID}, nil
}
