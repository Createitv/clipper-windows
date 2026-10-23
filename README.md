# Clipper Windows

Windows-only source snapshot for building the Clipper native desktop client.
This repository contains the WinUI/C# client, its required shared Go client
packages, two tray icons and the packaging workflow. It does not contain the
website, cloud server, other native platforms, private history or test data.
No original repository Git history is included. See SOURCE-MANIFEST.json.

## Download a development build

Open **Actions → Package Clipper Windows → the latest successful run → Artifacts**.
Download Clipper-Windows-x64 for Intel/AMD Windows, or Clipper-Windows-ARM64 for
Windows on ARM. The artifact contains a ZIP and its SHA-256 checksum. Extract
the complete application ZIP, then run **ClipWeave.exe**. Keep its Go helper and
all DLLs in the same directory. These are unsigned development builds, not a
signed installer or Microsoft Store release. ARM64 is cross-built on an x64
runner, not runtime-tested there. Builds alone do not prove clipboard/system
integration or cross-device synchronization works on physical devices.

## Build with GitHub Actions

The workflow runs on pushes to main affecting Windows build inputs, or can be
started with **Run workflow**. It builds x64 and ARM64 using Windows 2022, Go
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
