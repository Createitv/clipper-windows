param([ValidateSet('x64','ARM64')][string]$Architecture='x64')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

# The local build.ps1 keeps its external-volume requirement. Only an ephemeral,
# standard GitHub-hosted Windows runner may use this separate CI entry point.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows' -or -not $env:RUNNER_TEMP) {
    throw 'This entry point requires a GitHub-hosted Windows runner. Use build.ps1 locally.'
}
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$work=Join-Path $env:RUNNER_TEMP "clipper-windows-$Architecture"
$core=Join-Path $work 'core/clipweave-core.exe'
$publish=Join-Path $work 'publish'
$artifacts=Join-Path $work 'artifacts'
$env:GOPATH=Join-Path $work 'cache/go'
$env:GOMODCACHE=Join-Path $env:GOPATH 'pkg/mod'
$env:GOCACHE=Join-Path $work 'cache/go-build'
$env:GOTMPDIR=Join-Path $work 'tmp/go'
$env:NUGET_PACKAGES=Join-Path $work 'cache/nuget/packages'
$env:NUGET_HTTP_CACHE_PATH=Join-Path $work 'cache/nuget/http'
$env:NUGET_PLUGINS_CACHE_PATH=Join-Path $work 'cache/nuget/plugins'
$env:DOTNET_CLI_HOME=Join-Path $work 'cache/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:TMP=Join-Path $work 'tmp';$env:TEMP=$env:TMP
$env:GOOS='windows';$env:GOARCH=if($Architecture -eq 'ARM64'){'arm64'}else{'amd64'};$env:CGO_ENABLED='0'
New-Item -Force -ItemType Directory (Split-Path $core),$publish,$artifacts,$env:GOTMPDIR,$env:NUGET_PACKAGES,$env:DOTNET_CLI_HOME | Out-Null

Push-Location (Join-Path $repo 'native')
try {
    & go vet ./cmd/clipweave-core
    if($LASTEXITCODE -ne 0){throw 'Go vet failed'}
    & go build -trimpath -o $core ./cmd/clipweave-core
    if($LASTEXITCODE -ne 0){throw 'Go core build failed'}
} finally {Pop-Location}

# Hosted images also carry newer SDKs. Pin SDK 8 for this project and use
# Visual Studio MSBuild, which supplies the WinUI PRI resource build tasks.
'{"sdk":{"version":"8.0.100","rollForward":"latestFeature","allowPrerelease":false}}' | Set-Content (Join-Path $repo 'global.json') -Encoding utf8
& msbuild (Join-Path $PSScriptRoot 'ClipWeave.csproj') /restore /t:Publish /p:Configuration=Release "/p:RuntimeIdentifier=win-$($Architecture.ToLower())" /p:Platform=$Architecture "/p:ClipWeaveCorePath=$core" "/p:BaseIntermediateOutputPath=$work/obj/" "/p:BaseOutputPath=$work/bin/" "/p:PublishDir=$publish/"
if($LASTEXITCODE -ne 0){throw 'WinUI publish failed'}
foreach($name in @('ClipWeave.exe','clipweave-core.exe')) {
    if(-not (Test-Path (Join-Path $publish $name))){throw "Missing published executable: $name"}
}

# The x64 runner can execute the x64 helper. Use a fresh, isolated empty vault;
# this checks the real JSON pipe without contacting any account or service.
if($Architecture -eq 'x64') {
    $start=[Diagnostics.ProcessStartInfo]::new($core)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add('--vault-dir');$start.ArgumentList.Add((Join-Path $work 'smoke-vault'))
    $process=[Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.WriteLine('{"id":"ci-smoke","action":"status"}')
        $process.StandardInput.Close()
        $response=$process.StandardOutput.ReadToEndAsync()
        $diagnostics=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(15000)){$process.Kill();throw 'Go pipe smoke test timed out'}
        $result=$response.GetAwaiter().GetResult() | ConvertFrom-Json
        if($process.ExitCode -ne 0 -or $result.id -ne 'ci-smoke' -or $result.ok -ne $true){throw 'Go pipe smoke test failed'}
        $null=$diagnostics.GetAwaiter().GetResult()
    } finally {$process.Dispose()}
}

@"
Clipper Windows development build ($Architecture)
Commit: $env:GITHUB_SHA
Build: $env:GITHUB_RUN_NUMBER

Extract the complete ZIP into a folder and run ClipWeave.exe.
Keep clipweave-core.exe and all bundled DLLs beside the application.
This is an unsigned development build, not an installer or a Microsoft Store release.
ARM64 is cross-built and not runtime-tested on this x64 runner.
"@ | Set-Content (Join-Path $publish 'BUILD-INFO.txt') -Encoding utf8

$manifest=Get-ChildItem $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/')
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(),$relative
}
$manifest | Set-Content (Join-Path $publish 'SHA256SUMS.txt') -Encoding utf8
$zip=Join-Path $artifacts "Clipper-Windows-$Architecture.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
"{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(),(Split-Path $zip -Leaf) | Set-Content "$zip.sha256" -Encoding utf8
"artifact_path=$artifacts" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
"### Clipper Windows $Architecture`n`nUnsigned development ZIP built from ``$env:GITHUB_SHA``. Extract the whole archive, then run ``ClipWeave.exe``.`n" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
