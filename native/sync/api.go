package syncer

import (
	"bytes"
	"clipweave.local/device/protocol"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

type API struct {
	base          *url.URL
	client        *http.Client
	signingSecret string
}
type CloudError struct {
	Status int
	Code   string
}

func (e *CloudError) Error() string {
	switch e.Code {
	case "invalid_credentials":
		return "邮箱或密码不正确；未设置密码时可使用验证码登录"
	case "invalid_password":
		return "请设置 12–128 个字符的账户密码"
	case "invalid_code":
		return "验证码不正确或已过期，请重新获取"
	case "try_later":
		return "尝试过于频繁，请稍后重试"
	}
	return fmt.Sprintf("云同步：%s (%d)", e.Code, e.Status)
}
func NewAPI(endpoint string) (*API, error) {
	u, err := url.Parse(strings.TrimSpace(endpoint))
	if err != nil || u.Host == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" {
		return nil, errors.New("同步地址无效，不能包含凭据、查询参数或片段")
	}
	local := u.Hostname() == "localhost" || u.Hostname() == "127.0.0.1" || u.Hostname() == "::1"
	if u.Scheme != "https" && !(u.Scheme == "http" && local) {
		return nil, errors.New("同步服务必须使用 HTTPS；HTTP 仅允许本机开发服务")
	}
	return &API{base: u, client: &http.Client{Timeout: 60 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}}, nil
}
func (a *API) sign(req *http.Request, token string) error {
	if a.signingSecret == "" || token == "" {
		return nil
	}
	key, err := base64.StdEncoding.DecodeString(a.signingSecret)
	if err != nil || len(key) != ed25519.PrivateKeySize {
		return errors.New("设备签名密钥无效")
	}
	var data []byte
	if req.GetBody != nil {
		body, e := req.GetBody()
		if e != nil {
			return e
		}
		defer body.Close()
		data, e = io.ReadAll(io.LimitReader(body, 5*1024*1024+1))
		if e != nil {
			return e
		}
		if len(data) > 5*1024*1024 {
			return errors.New("请求过大")
		}
	}
	nonce := make([]byte, 16)
	if _, err = rand.Read(nonce); err != nil {
		return err
	}
	protocol.SignRequest(req, data, token, ed25519.PrivateKey(key), time.Now(), hex.EncodeToString(nonce))
	return nil
}
func (a *API) request(ctx context.Context, method, path, token string, body io.Reader) (*http.Response, error) {
	ref, err := url.Parse(path)
	if err != nil || ref.IsAbs() || !strings.HasPrefix(path, "/") {
		return nil, errors.New("非法 API 路径")
	}
	req, err := http.NewRequestWithContext(ctx, method, a.base.ResolveReference(ref).String(), body)
	if err != nil {
		return nil, err
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	req.Header.Set("Content-Type", "application/json")
	if err := a.sign(req, token); err != nil {
		return nil, err
	}
	res, err := a.client.Do(req)
	if err != nil {
		return nil, errors.New("无法连接同步服务，请检查网络后重试")
	}
	return res, nil
}
func (a *API) call(ctx context.Context, method, path, token string, data, out any) error {
	var body io.Reader
	if data != nil {
		b, err := json.Marshal(data)
		if err != nil {
			return err
		}
		body = bytes.NewReader(b)
	}
	res, err := a.request(ctx, method, path, token, body)
	if err != nil {
		return err
	}
	defer res.Body.Close()
	b, err := io.ReadAll(io.LimitReader(res.Body, 16*1024*1024+1))
	if err != nil {
		return err
	}
	if len(b) > 16*1024*1024 {
		return errors.New("服务器响应过大")
	}
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		var e struct {
			Error string `json:"error"`
		}
		_ = json.Unmarshal(b, &e)
		if e.Error == "" || len(e.Error) > 128 {
			e.Error = "request_failed"
		}
		return &CloudError{res.StatusCode, e.Error}
	}
	if out == nil {
		return nil
	}
	if err := json.Unmarshal(b, out); err != nil {
		return errors.New("同步服务返回了无法识别的数据")
	}
	return nil
}
func (a *API) upload(ctx context.Context, id, token string, data []byte) error {
	req, err := http.NewRequestWithContext(ctx, "PUT", a.base.ResolveReference(&url.URL{Path: "/assets/" + id}).String(), bytes.NewReader(data))
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Content-Type", "application/octet-stream")
	if err := a.sign(req, token); err != nil {
		return err
	}
	res, err := a.client.Do(req)
	if err != nil {
		return errors.New("附件上传连接失败")
	}
	defer res.Body.Close()
	_, _ = io.Copy(io.Discard, io.LimitReader(res.Body, 4096))
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		return &CloudError{res.StatusCode, "asset_upload_failed"}
	}
	return nil
}
func (a *API) download(ctx context.Context, id, token string, max int64) ([]byte, error) {
	res, err := a.request(ctx, "GET", "/assets/"+id, token, nil)
	if err != nil {
		return nil, err
	}
	defer res.Body.Close()
	if res.StatusCode != 200 {
		return nil, &CloudError{res.StatusCode, "asset_download_failed"}
	}
	data, err := io.ReadAll(io.LimitReader(res.Body, max+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) > max {
		return nil, errors.New("附件响应超过声明大小")
	}
	return data, nil
}
