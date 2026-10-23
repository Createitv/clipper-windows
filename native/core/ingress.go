package core

import (
	"bytes"
	"errors"
	"net/url"
	"strings"
	"time"
)

// ImportShared records the inbox receipt and the new clip in ONE encrypted commit.
// Replaying a packet after a crash or an inbox unlink failure never duplicates it,
// including if the user has since deleted the original imported clip.
func (v *Vault) ImportShared(packetID, text string, data []byte, name string) (Object, error) {
	var result Object
	err := v.mutate(func() error {
		if !validID(packetID) {
			return errors.New("invalid share packet id")
		}
		if id := v.state.Ingress[packetID]; id != "" {
			result = Object{"clipId": id, "imported": false}
			return nil
		}
		id, err := newID()
		if err != nil {
			return err
		}
		var clip Object
		if name != "" {
			if len(data) > 20*1024*1024 {
				return errors.New("shared attachment exceeds 20 MiB")
			}
			key, err := randomKey()
			if err != nil {
				return err
			}
			parts, size, err := v.chunk(bytes.NewReader(data), key, id)
			if err != nil {
				return err
			}
			clip = v.fileClip(id, key, name, size, parts)
		} else {
			if text == "" || len(text) > 32*1024 {
				return errors.New("shared text must be nonempty and at most 32 KiB")
			}
			kind := "text"
			if u, e := url.Parse(text); e == nil && u.Scheme != "" {
				kind = "link"
			}
			clip = Object{"id": id, "type": kind, "text": text, "title": strings.SplitN(text, "\n", 2)[0], "created": time.Now().UnixMilli(), "favourite": false, "group": "", "author": v.state.DeviceID}
		}
		if err = v.addClip(clip); err != nil {
			return err
		}
		if v.state.Ingress == nil {
			v.state.Ingress = map[string]string{}
		}
		v.state.Ingress[packetID] = id
		result = Object{"clipId": id, "imported": true}
		return nil
	})
	return result, err
}
