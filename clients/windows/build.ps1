param([ValidateSet('x64','ARM64')][string]$Architecture='x64')
$ErrorActionPreference='Stop'
if (-not $env:CLIPWEAVE_DEV_ROOT -or -not (Test-Path $env:CLIPWEAVE_DEV_ROOT)) { throw 'Set CLIPWEAVE_DEV_ROOT to the connected external development disk.' }
$marker=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Environment/volume-8AC0A789-D8C5-4B8A-BC55-DC782501C2F4'
if(-not (Test-Path $marker)){throw 'External development-volume marker is missing; no local cache fallback is allowed.'}
$root=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Projects/ClipWeave/native-build/windows'
$env:NUGET_PACKAGES=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Caches/nuget/packages'
$env:DOTNET_CLI_HOME=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Caches/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Caches/nuget/http'
$env:NUGET_PLUGINS_CACHE_PATH=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Caches/nuget/plugins'
$env:TMP=Join-Path $env:CLIPWEAVE_DEV_ROOT 'tmp'; $env:TEMP=$env:TMP
$env:GOPATH=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Caches/go'; $env:GOCACHE=Join-Path $env:CLIPWEAVE_DEV_ROOT 'Caches/go-build'
$env:GOMODCACHE=Join-Path $env:GOPATH 'pkg/mod'; $env:GOTMPDIR=Join-Path $env:TMP 'go'
New-Item -Force -ItemType Directory $root,$env:NUGET_PACKAGES,$env:DOTNET_CLI_HOME,$env:GOTMPDIR | Out-Null
$repo=Resolve-Path (Join-Path $PSScriptRoot '../..')
$core=Join-Path $root "core-$Architecture/clipweave-core.exe"
New-Item -Force -ItemType Directory (Split-Path $core) | Out-Null
$env:GOOS='windows';$env:GOARCH=if($Architecture -eq 'ARM64'){'arm64'}else{'amd64'};$env:CGO_ENABLED='0'
Push-Location (Join-Path $repo 'native')
try { & go build -trimpath -o $core ./cmd/clipweave-core; if($LASTEXITCODE -ne 0){throw 'Go core build failed'} } finally {Pop-Location}
& dotnet publish (Join-Path $PSScriptRoot 'ClipWeave.csproj') -c Release -r "win-$($Architecture.ToLower())" -p:Platform=$Architecture "-p:ClipWeaveCorePath=$core" "-p:BaseIntermediateOutputPath=$root/obj-$Architecture/" "-p:BaseOutputPath=$root/bin-$Architecture/" -o "$root/publish-$Architecture"
if($LASTEXITCODE -ne 0){throw 'WinUI publish failed'}
