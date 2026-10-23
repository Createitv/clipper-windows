package core

import (
	"errors"
	"time"
)

// Membership comes from the authenticated sync API; config RPC does not accept it.
// Limits on encrypted fields are evaluated here without exposing plaintext to the cloud.
func (v *Vault) membershipLimits() Object {
	access := objectValue(v.state.Config["membership"])
	limits := objectValue(access["limits"])
	return limits
}
func (v *Vault) memberLimit(key string) int64 { return integer(v.membershipLimits()[key]) }
func (v *Vault) checkClipLimits(c Object) error {
	if max := v.memberLimit("attachmentBytes"); max > 0 && integer(c["size"]) > max {
		return errors.New("attachment_limit_reached")
	}
	if c["favourite"] == true && v.state.Clips[str(c["id"])]["favourite"] != true {
		n := int64(0)
		for id, x := range v.state.Clips {
			if id != str(c["id"]) && x["deleted"] != true && x["favourite"] == true {
				n++
			}
		}
		if max := v.memberLimit("favouriteLimit"); max > 0 && n >= max {
			return errors.New("favourite_limit_reached")
		}
	}
	return v.checkGroupLimit(str(c["group"]))
}
func (v *Vault) checkGroupLimit(name string) error {
	if name == "" {
		return nil
	}
	for _, x := range v.state.Groups {
		if x == name {
			return nil
		}
	}
	if max := v.memberLimit("groupLimit"); max > 0 && int64(len(v.state.Groups)) >= max {
		return errors.New("group_limit_reached")
	}
	return nil
}
func (v *Vault) historyVisible(c Object) bool {
	days := v.memberLimit("historyDays")
	// Do not silently delete a customer's encrypted history on downgrade.
	// Older history remains exportable; the normal timeline shows the plan's window.
	return days <= 0 || c["favourite"] == true || integer(c["created"]) >= time.Now().Add(-time.Duration(days)*24*time.Hour).UnixMilli()
}

func objectValue(value any) Object { v, _ := value.(map[string]any); return v }
