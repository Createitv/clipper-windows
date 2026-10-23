package syncer

import (
	"clipweave.local/device/core"
	"context"
	"encoding/hex"
	"errors"
	"strings"
)

const pairingQRPrefix = "cliper-pair-v1:"

func pairingQR(code, fingerprint string) string { return pairingQRPrefix + code + ":" + fingerprint }

func parsePairingQR(value string) (string, string, error) {
	parts := strings.Split(strings.TrimSpace(value), ":")
	if len(parts) != 3 || parts[0] != "cliper-pair-v1" {
		return "", "", errors.New("无效的设备授权二维码")
	}
	code, err := normalizePairingCode(parts[1])
	if err != nil {
		return "", "", err
	}
	fingerprint := strings.ToUpper(parts[2])
	decoded, err := hex.DecodeString(fingerprint)
	if err != nil || len(decoded) != 8 {
		return "", "", errors.New("无效的设备授权二维码")
	}
	return code, fingerprint, nil
}

// Scanning on a trusted device is consent. Bind approval to the public-key
// fingerprint displayed by the requesting device instead of approving a code
// alone. ApprovePairing retains account, vault, expiry and trusted-device checks.
func (e *Engine) ApprovePairingQR(ctx context.Context, payload, pass string) (core.Object, error) {
	code, expected, err := parsePairingQR(payload)
	if err != nil {
		return nil, err
	}
	return e.ApprovePairing(ctx, code, pass, expected)
}

// QR codes contain the same public locator as manual entry, never a vault key.
func normalizePairingCode(value string) (string, error) {
	code := strings.ToUpper(strings.Map(func(r rune) rune {
		switch r {
		case ' ', '\t', '\r', '\n', '-':
			return -1
		}
		return r
	}, value))
	if len(code) != 12 {
		return "", errors.New("请输入完整的 12 位授权码")
	}
	for _, r := range code {
		if !strings.ContainsRune("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", r) {
			return "", errors.New("请输入完整的 12 位授权码")
		}
	}
	return code, nil
}
