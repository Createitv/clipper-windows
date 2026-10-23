param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows'){throw 'UI tests require a fresh GitHub-hosted Windows profile; do not run against a personal vault.'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipperUiNative {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className,string windowName);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);
}
'@
New-Item -Force -ItemType Directory $OutputDirectory | Out-Null
$gui=Start-Process $Executable -ArgumentList '--settings' -PassThru
function SettingsWindow {
    $deadline=(Get-Date).AddSeconds(25)
    do {
        $gui.Refresh();if($gui.HasExited){throw "Clipper exited: $($gui.ExitCode)"}
        $windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$gui.Id))
        foreach($window in $windows){if($window.Current.Name -match 'Settings|设置'){return $window}}
        Start-Sleep -Milliseconds 400
    } while((Get-Date) -lt $deadline)
    throw 'Settings did not become visible'
}
function Control($window,[string]$id) {
    $deadline=(Get-Date).AddSeconds(10)
    do {
        $found=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
        if($null -ne $found){return $found};Start-Sleep -Milliseconds 200
    } while((Get-Date) -lt $deadline)
    throw "Missing settings control: $id"
}
function InvokeControl($control){$control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 400}
try {
    $window=SettingsWindow
    foreach($tab in @('general','account','authorization','membership','privacy','about')){
        InvokeControl (Control $window "settings-tab-$tab")
        Write-Output "Settings page opened: $tab"
        if($tab -eq 'authorization'){
            $bounds=(Control $window 'settings-auth-code').Current.BoundingRectangle
            if($bounds.Right -gt $window.Current.BoundingRectangle.Right -or $bounds.Left -lt $window.Current.BoundingRectangle.Left){throw 'Authorization form exceeds the visible window'}
        }
        # Empty hosted profile only. Screenshot failure is diagnostic, not a replacement for UI assertions.
        try {
            $rect=$window.Current.BoundingRectangle
            $bitmap=[System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
            $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
            try{$graphics.CopyFromScreen([int]$rect.Left,[int]$rect.Top,0,0,$bitmap.Size);$bitmap.Save((Join-Path $OutputDirectory "$tab.png"),[System.Drawing.Imaging.ImageFormat]::Png)}finally{$graphics.Dispose();$bitmap.Dispose()}
        } catch {Write-Output 'Screenshot unavailable on runner desktop'}
    }
    InvokeControl (Control $window 'settings-tab-general')
    $slider=(Control $window 'settings-height').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $slider.SetValue(520);Start-Sleep -Milliseconds 800
    if($slider.Current.Value -ne 520){throw 'Panel height control did not retain value'}
    [ClipperUiNative]::PostMessage([IntPtr]$window.Current.NativeWindowHandle,0x0010,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 3;$gui.Refresh();if($gui.HasExited){throw 'Closing settings terminated the resident app'}
    $main=[ClipperUiNative]::FindWindow($null,'Clipper');[uint32]$mainPid=0
    if($main -eq [IntPtr]::Zero){throw 'Resident main window was destroyed'}
    [ClipperUiNative]::GetWindowThreadProcessId($main,[ref]$mainPid) | Out-Null
    if($mainPid -ne $gui.Id){throw 'Unexpected resident window owner'}
    [ClipperUiNative]::PostMessage($main,0x0010,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2;$gui.Refresh();if($gui.HasExited){throw 'Closing the panel terminated the app'}
    $second=Start-Process $Executable -ArgumentList '--settings' -PassThru
    $window=SettingsWindow
    InvokeControl (Control $window 'settings-tab-general')
    $slider=(Control $window 'settings-height').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    if($slider.Current.Value -ne 520){throw 'Reopened settings lost height preference'}
    InvokeControl (Control $window 'settings-tab-about')
    InvokeControl (Control $window 'settings-quit')
    if(-not $gui.WaitForExit(15000)){throw 'Explicit Quit did not terminate the app'}
    'Settings pages, height preference, close-to-background, reopen and explicit Quit passed' | Set-Content (Join-Path $OutputDirectory 'result.txt') -Encoding utf8
    Write-Output 'Settings pages, height preference, close-to-background, reopen and explicit Quit passed'
} finally {
    $log=Join-Path $env:LOCALAPPDATA 'ClipWeave/ui-errors.log'
    if(Test-Path $log){Get-Content $log | Write-Output}
    $gui.Refresh();if(-not $gui.HasExited){Stop-Process -Id $gui.Id -Force}
    Get-Process -Name 'clipweave-core' -ErrorAction SilentlyContinue | Stop-Process -Force
}
