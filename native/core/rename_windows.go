package core

import "golang.org/x/sys/windows"

func renameNoReplace(from, to string) error {
	source, err := windows.UTF16PtrFromString(from)
	if err != nil {
		return err
	}
	target, err := windows.UTF16PtrFromString(to)
	if err != nil {
		return err
	}
	// Flags=0 rejects existing destinations and never copies across volumes.
	return windows.MoveFileEx(source, target, 0)
}
