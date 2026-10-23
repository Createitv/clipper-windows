# Clipper Windows

Windows-only source snapshot for building the Clipper native desktop client.
This repository contains the WinUI/C# client, its required shared Go client
packages, brand artwork, tray icons and the packaging workflow. It does not contain the
website, cloud server, other native platforms, private history or test data.
No original repository Git history is included. See SOURCE-MANIFEST.json.

## Download a development build

Open **Releases** and download **Clipper-Windows-x64.zip** for Intel/AMD Windows.
Only one Windows architecture is published. Its SHA-256 checksum is also available.
Development artifacts are also available under Actions. Extract
the complete application ZIP, then run **Clipper.exe**. Keep its Go helper and
all DLLs in the same directory. These are unsigned development builds, not a
signed installer or Microsoft Store release. This is not a native ARM64 build. Builds alone do not prove clipboard/system
integration or cross-device synchronization works on physical devices.

## Build with GitHub Actions

The workflow runs on pushes to main affecting Windows build inputs, or can be
started with **Run workflow**. It builds x64 using Windows 2022, Go
from native/go.mod and .NET 8. It publishes self-contained WinUI binaries and
runs a real JSON-pipe smoke check of the x64 Go helper with an isolated empty
vault. No production secrets, account sign-in or cloud credentials are needed.
Artifacts are retained for 3 days; Go/NuGet caches are not uploaded.

Standard GitHub-hosted runner minutes are free for public repositories;
artifact storage and other GitHub limits still apply. See
https://docs.github.com/en/billing/concepts/product-billing/github-actions.

## Local Windows build

Install Go 1.25, .NET 8 SDK and Windows/WinUI build tools. The local build.ps1
uses the original project's connected external development-volume policy;
CLIPWEAVE_DEV_ROOT must point to that volume, including its Environment marker.
Run `./clients/windows/build.ps1 -Architecture x64` (or ARM64). Local output and
caches stay on that development volume. The separate ci-build.ps1 accepts only
an ephemeral GitHub-hosted Windows runner; it does not bypass local checks.

Source is published for the Windows build. No open-source license grant is
added by this snapshot; third-party dependencies retain their own licenses.

## Native Mac design on Windows

The panel appears above the Windows taskbar, with the same light palette,
Clipper branding, search toolbar, group tabs and horizontal content cards as
our native Mac app. Use Ctrl+Shift+V or the tray icon to open it. Escape hides
the panel; the core keeps running until you choose Quit Clipper.

Search and type filters combine with Recent, Favorites or a custom group.
Use arrows to select, Space to preview, Enter or double-click to copy, and
Ctrl+1 through Ctrl+9 to copy a visible item. Right-click a card to preview,
export, favorite, move or delete it. Copy restores the previous app and can
paste automatically; General settings lets you disable this.

Settings are split into General, Account and devices, Authorization and
recovery, Membership, Privacy and storage, and About. Accounts, encryption,
retention, membership and synchronization continue to use the shared Go core.
Authorize devices with a QR code or the 12-character code; camera access is
optional. Keep recovery files private. Password-manager exclusion formats and
Windows clipboard-history exclusion flags are respected during capture.

Window integration uses WinUIEx 2.6.0 and the tray uses H.NotifyIcon.WinUI
2.2.0, alongside Windows App SDK 1.7 and ZXing.Net. A packaged startup check
supplements presentation/QR/localization tests; camera use, tray behavior,
multi-monitor display and paste into other apps still require Windows hardware
acceptance. Windows membership management opens the web account billing page.

## Refresh and settings fixes

Background polling no longer disables the panel. Unchanged history retains
its item instances and scroll position, and only changed cards are patched.
Replies from the Go helper are read in bounded chunks and parsed away from
the UI thread. Thumbnail browsing uses local attachments only; full preview,
copy and export remain the actions that can download an attachment.

Resize the panel from its top grip or use **General → Panel height**. Height
is saved, clamped to the screen work area and always anchored above the
taskbar. The app consistently uses a light palette, including native chrome.

Settings use six toolbar categories and grouped forms, matching the Mac
structure, with devices/revocation, sign-in events, QR/manual authorization,
recovery files, server-verified membership, retention and attachment cleanup.
You may open settings with `Clipper.exe --settings`, including before sign-in.
Closing settings or the panel keeps the application resident. **Quit Clipper**
is the explicit exit action. CI exercises these paths with Windows UI
automation in a fresh hosted profile; never run its UI test script against
a personal vault.
