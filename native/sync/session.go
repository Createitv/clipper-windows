package syncer

import (
	"clipweave.local/device/core"
	"time"
)

// SessionAuthorized preserves offline access until a stored session expires.
// Missing expiry supports previously enrolled legacy sessions.
func SessionAuthorized(config core.Object) bool {
	if value(config["token"]) == "" {
		return false
	}
	expires := number(config["sessionExpires"])
	return expires <= 0 || expires > time.Now().UnixMilli()
}

// CloudReady means this account has completed a full cloud round trip. A token
// alone never grants access to a standalone local library.
func CloudReady(config core.Object) bool {
	return SessionAuthorized(config) && value(config["accountId"]) != "" && value(config["endpoint"]) != "" && number(config["cloudSyncedAt"]) > 0
}
