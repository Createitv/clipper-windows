using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace ClipWeave;
public sealed partial class MainWindow : Window {
    readonly GoClient core=new();
    readonly Grid body=new();
    readonly Grid shellRoot=new(){Background=Palette.White,RequestedTheme=ElementTheme.Light};
    readonly ContentControl viewport=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    readonly TextBlock state=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock error=new(){TextWrapping=TextWrapping.Wrap,Foreground=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed)};
    readonly ListView history=new();
    readonly TextBox search=new(){PlaceholderText=L10n.Text("搜索你的剪贴板")};
    string activeGroup="",activeKind="";
    JsonObject status=new();
    List<JsonObject> clips=new();
    bool busy;
    bool triedAutoUnlock;
    JsonObject? pairing;
    bool closed;
    string lastCaptured="";
    string pendingRecovery="";
    readonly HashSet<string> previewDirectories=new();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer refreshTimer;
    readonly SemaphoreSlim operations=new(1,1);
    internal readonly WindowsPreferences Preferences=WindowsPreferences.Load();
    internal WindowsShell Shell {get;}
    internal Grid Root=>shellRoot;
    internal bool DialogOpen=>dialogDepth>0;
    int dialogDepth;
    SettingsWindow? settingsWindow;
    LoginWindow? loginWindow;
    bool manualLogin;
    Window? previewWindow;
    internal event System.Action? StateChanged;
    internal JsonObject Status=>status;
    internal string LastError=>error.Text;
    internal Window DialogOwner=>loginWindow?.AppWindow.IsVisible==true?loginWindow:settingsWindow?.AppWindow.IsVisible==true?settingsWindow:previewWindow?.AppWindow.IsVisible==true?previewWindow:this;
    internal XamlRoot DialogRoot=>((FrameworkElement)DialogOwner.Content).XamlRoot;
    public MainWindow() {
        Title="Clipper"; viewport.Content=body;
        shellRoot.Children.Add(new Border{Background=Palette.White,BorderBrush=Palette.Border,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(20),Child=viewport});Content=shellRoot;
        Shell=new WindowsShell(this);
        refreshTimer=DispatcherQueue.CreateTimer();refreshTimer.Interval=TimeSpan.FromSeconds(2);
        refreshTimer.Tick+=async(_,_)=>{if(!busy&&!closed)await Run(Refresh);};refreshTimer.Start();
        Closed+=async(_,_)=>{closed=true;refreshTimer.Stop();Shell.Dispose();settingsWindow?.Close();loginWindow?.Close();previewWindow?.Close();ClearPreviews();await core.DisposeAsync();};
        _=Run(async()=>{await Refresh();if(Environment.GetCommandLineArgs().Contains("--settings"))OpenSettings();else if(AccountReady)Shell.ShowPanel();else ShowLogin();});
    }
    internal Task Execute(Func<Task> operation)=>Run(operation);
    async Task Run(Func<Task> operation) {
        if(closed)return;await operations.WaitAsync();
        try {
            if(closed)return;busy=true;error.Text="";
            try{await operation();}
            catch(Exception e){if(e is not BridgeException)UiDiagnostics.Record("main-operation",e);if(e is BridgeException failure&&failure.Payload!=null){await SaveRecovery(failure.Payload);try{await Refresh();}catch{}}error.Text=L10n.Text(e.Message);SetNotice(error.Text);}
        } finally{busy=false;operations.Release();StateChanged?.Invoke();}
    }
    internal Task<JsonNode?> CallCore(string action,JsonObject? args=null)=>core.Call(action,args);
    internal Task RefreshData()=>Refresh();
    internal void RefreshInBackground(){if(!busy&&!closed)_=Run(Refresh);}
    internal void SetPanelHeight(double height)=>Shell.SetPanelHeight(height);
    internal Task SyncNow()=>Action("sync");
    internal bool CanUnlock=>status["exists"]?.GetValue<bool>()==true&&DeviceSecret.Read()!=null;
    internal async Task UnlockDevice(){var secret=DeviceSecret.Read()??throw new IOException(L10n.Text("请输入原资料库口令"));await core.Call("unlock",new(){["passphrase"]=secret});await Refresh();if(!AccountReady)throw new IOException(L10n.Text("请重新登录账户"));loginWindow?.Close();Shell.ShowPanel();}
    internal async Task ToggleCapture(){var config=status["config"] as JsonObject??new();await SaveConfig(new(){["captureEnabled"]=config["captureEnabled"]?.GetValue<bool>()!=true});}
    internal async Task SaveConfig(JsonObject values){await core.Call("config",new(){["values"]=values});await Refresh();}
    internal void OpenSettings(){
        try{settingsWindow??=new SettingsWindow(this);settingsWindow.AppWindow.Show();settingsWindow.Activate();Shell.ActivateWindow(settingsWindow);Shell.HidePanel();}
        catch(Exception failure){UiDiagnostics.Record("open-settings",failure);settingsWindow=null;SetNotice(L10n.Text(failure.Message));if(AccountReady)Shell.ShowPanel();else ShowLogin();}
    }
    internal void SettingsClosed(){settingsWindow=null;}
    internal void ShowLogin(bool manual=false){if(closed)return;if(manual)manualLogin=true;Shell.HidePanel();loginWindow??=new LoginWindow(this);loginWindow.Activate();}
    internal void LoginClosed(){loginWindow=null;manualLogin=false;}
    internal void SetNotice(string text){state.Text=text;}
    internal void FocusSearch(){search.Focus(FocusState.Programmatic);}
    Button Button(string title,Func<Task> action) { var button=new Button{Content=title}; button.Click+=async (_,_)=>await Run(action);return button; }
    async Task Action(string name,JsonObject? args=null) {await core.Call(name,args);await Refresh();}
    static string Str(JsonObject value,string key,string fallback="")=> key=="status" ? L10n.Text(value[key]?.ToString() ?? fallback) : value[key]?.ToString() ?? fallback;
    bool sessionRemembered;
    internal bool AccountReady => status["unlocked"]?.GetValue<bool>()==true && (status["config"] as JsonObject)?["connected"]?.GetValue<bool>()==true;
    async Task Refresh() {
        var wasUnlocked=AccountReady;
        status=await core.Call("requireAccount") as JsonObject ?? new();
        if(!triedAutoUnlock) {triedAutoUnlock=true;if(status["exists"]?.GetValue<bool>()==true&&status["unlocked"]?.GetValue<bool>()!=true&&DeviceSecret.Read() is string pass) {await core.Call("unlock",new(){["passphrase"]=pass});status=await core.Call("status") as JsonObject ?? new();}}
        if(status["unlocked"]?.GetValue<bool>()==true) sessionRemembered=AccountReady;
        if(AccountReady) {
            var id=Str(status,"vaultId",Str(status["config"] as JsonObject??new(),"accountId"));
            if(id.Length>0&&!Preferences.AppliedDefaults.Contains(id)) {
                await core.Call("config",new(){["values"]=new JsonObject{["captureEnabled"]=true,["directPaste"]=true}});
                Preferences.AppliedDefaults.Add(id);Preferences.Save();status=await core.Call("status") as JsonObject??new();
            }
            await CaptureClipboard();
        }
        if(!AccountReady){thumbnailEpoch++;thumbnails.Clear();thumbnailMisses.Clear();historyItems.Clear();visibleClips.Clear();historySignature="";previewWindow?.Close();}
        clips=AccountReady ? (await core.Call("list") as JsonArray ?? new()).OfType<JsonObject>().ToList() : new();
        if(body.Children.Count==0||wasUnlocked!=AccountReady) Render();
        else { UpdateSyncIndicator();if(AppWindow.IsVisible){if(renderedGroups!=(status["groups"]?.ToJsonString()??""))UpdateGroups();RenderHistory();} }
        if(wasUnlocked&&!AccountReady){settingsWindow?.Close();ShowLogin();}
        if(AccountReady&&loginWindow!=null&&!loginWindow.IsBusy&&!manualLogin){loginWindow.Close();Shell.ShowPanel();}
        StateChanged?.Invoke();
    }
    internal async Task<ContentDialogResult> Dialog(string title,UIElement content,string primary) {
        dialogDepth++;
        try{return await new ContentDialog{Title=title,Content=content,PrimaryButtonText=primary,CloseButtonText=L10n.Text("取消"),XamlRoot=DialogRoot}.ShowAsync();}
        finally{dialogDepth--;}
    }
    async Task SaveRecovery(JsonObject? result) {
        var generated=result?["recovery"]?.ToString();if(!string.IsNullOrEmpty(generated)&&pendingRecovery.Length==0)pendingRecovery=generated;var key=pendingRecovery;if(key.Length==0)return;lastCaptured=key;
        var picker=new FileSavePicker{SuggestedFileName="ClipWeave-Recovery"};picker.FileTypeChoices.Add("Recovery JSON",new List<string>{".json"});InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(DialogOwner));var file=await picker.PickSaveFileAsync();
        if(file!=null){await Windows.Storage.FileIO.WriteTextAsync(file,key);pendingRecovery="";}

    }
    async Task ImportFile() {
        var picker=new FileOpenPicker();picker.FileTypeFilter.Add("*");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(DialogOwner));var file=await picker.PickSingleFileAsync();
        if(file!=null)await Action("importFile",new(){["path"]=file.Path});
    }
    async Task ImportFolder() {
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(DialogOwner));var folder=await picker.PickSingleFolderAsync();
        if(folder!=null)await Action("importFolder",new(){["path"]=folder.Path});
    }
    void ClearPreviews() {
        foreach(var directory in previewDirectories.ToArray()) {
            try { Directory.Delete(directory,true);previewDirectories.Remove(directory); }
            catch(IOException) { /* A native viewer may still hold its file handle; retry on close. */ }
            catch(UnauthorizedAccessException) { }
        }
    }
    Task Settings(){OpenSettings();return Task.CompletedTask;}
    Task Connect(){ShowLogin();return Task.CompletedTask;}
    internal async Task<JsonObject> SignIn(string email,string password,string code,bool passwordMode) {
        JsonObject result;
        try{result=await core.Call(passwordMode?"passwordConnect":"autoConnect",new(){["endpoint"]="https://api.cliper.dev",["email"]=email,["password"]=password,["code"]=code,["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject??new();}
        catch(BridgeException failure) when(failure.Payload!=null){await SaveRecovery(failure.Payload);throw;}
        if(result["passwordSetupRequired"]?.GetValue<bool>()==true)return result;
        pairing=result["pairing"] as JsonObject;await SaveRecovery(result);await Refresh();
        if(pairing!=null)await Authorization();
        if(AccountReady){loginWindow?.Close();Shell.ShowPanel();}
        return result;
    }
    internal Task ShowAuthorization()=>Authorization();
    internal async Task ImportRecoveryFile(){var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".json");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(DialogOwner));var file=await picker.PickSingleFileAsync();if(file!=null){await core.Call("importRecovery",new(){["text"]=await Windows.Storage.FileIO.ReadTextAsync(file),["passphrase"]=DeviceSecret.GetOrCreate()});await Refresh();}}
    internal async Task ExportRecoveryFile()=>await SaveRecovery(await core.Call("exportRecovery",new(){["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject);
    internal async Task ScanAuthorization(){var scanned=await AuthorizationScanner.ScanAsync(DialogRoot);if(scanned==null)return;if(scanned.StartsWith("cliper-pair-v1:")){await core.Call("approvePairingQR",new(){["text"]=scanned,["passphrase"]=DeviceSecret.GetOrCreate()});await Dialog(L10n.Text("授权成功"),Palette.Text(L10n.Text("新设备将自动连接并同步。")),L10n.Text("完成"));}else await AuthorizeCode(scanned);}
    internal async Task AuthorizeCode(string text){var code=AuthorizationCode.Normalize(text)??throw new IOException(L10n.Text("请输入完整的 12 位授权码"));var request=await core.Call("inspectPairing",new(){["code"]=code}) as JsonObject??new();var fingerprint=Str(request,"fingerprint");if(await Dialog(L10n.Text("核对请求设备指纹"),Palette.Text(Str(request,"kind")+"\n"+fingerprint+L10n.Text("\n仅当请求设备显示相同指纹时授权。")),L10n.Text("指纹一致，授权"))==ContentDialogResult.Primary)await core.Call("approvePairing",new(){["code"]=code,["text"]=fingerprint,["passphrase"]=DeviceSecret.GetOrCreate()});}

    internal Task ShowDevices()=>Devices();
    async Task Authorization() {
        pairing=status["pendingPairing"] as JsonObject ?? pairing;
        var panel=new StackPanel{Spacing=10};var authNotice=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(authNotice);
        var code=new TextBox{Header=L10n.Text("12 位授权码"),PlaceholderText="ABCD-2345-WXYZ"};
        var import=new Button{Content=L10n.Text("导入恢复文件…")};import.Click+=async(_,_)=>{try {var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".json");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(DialogOwner));var file=await picker.PickSingleFileAsync();if(file!=null){var text=await Windows.Storage.FileIO.ReadTextAsync(file);await core.Call("importRecovery",new(){["text"]=text,["passphrase"]=DeviceSecret.GetOrCreate()});await Refresh();}}catch(Exception e){authNotice.Text=L10n.Text(e.Message);}};panel.Children.Add(import);
        var export=new Button{Content=L10n.Text("导出恢复文件…")};export.Click+=async(_,_)=>{try{await SaveRecovery(await core.Call("exportRecovery",new(){["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject);}catch(Exception e){authNotice.Text=L10n.Text(e.Message);}};panel.Children.Add(export);
        if(pairing!=null) {
            await WaitForAuthorization(panel);
            return;
        }
        panel.Children.Insert(0,new TextBlock{Text=L10n.Text("扫描新设备的二维码，或输入 12 位授权码。"),TextWrapping=TextWrapping.Wrap});
        panel.Children.Insert(1,code);
        bool scanning=false;
        var scan=new Button{Content=L10n.Text("扫描授权二维码")};panel.Children.Insert(2,scan);
        var authorization=new ContentDialog{Title=L10n.Text("授权与恢复"),Content=panel,PrimaryButtonText=L10n.Text("检查请求"),CloseButtonText=L10n.Text("取消"),XamlRoot=DialogRoot};
        scan.Click+=(_,_)=>{scanning=true;authorization.Hide();};
        if(status["unlocked"]?.GetValue<bool>()==true) {
            var data=await core.Call("pairingGrants");var grants=data as JsonArray ?? (data as JsonObject)?["requests"] as JsonArray ?? new();
            foreach(var grant in grants.OfType<JsonObject>()) {var id=Str(grant,"id");var cancel=new Button{Content=L10n.Text("取消浏览器授权 ")+id};cancel.Click+=async(_,_)=>{try{await core.Call("cancelGrant",new(){["deviceId"]=id});cancel.IsEnabled=false;}catch(Exception e){authNotice.Text=L10n.Text(e.Message);}};panel.Children.Add(cancel);}
        }
        var choice=await authorization.ShowAsync();
        if(scanning) {
            var scanned=await AuthorizationScanner.ScanAsync(DialogRoot);if(scanned==null)return;
            if(scanned.StartsWith("cliper-pair-v1:")) {await core.Call("approvePairingQR",new(){["text"]=scanned,["passphrase"]=DeviceSecret.GetOrCreate()});await Dialog(L10n.Text("授权成功"),new TextBlock{Text=L10n.Text("新设备将自动连接并同步。")},L10n.Text("完成"));return;}
            code.Text=scanned;
        }
        else if(choice!=ContentDialogResult.Primary)return;
        var normalized=AuthorizationCode.Normalize(code.Text) ?? throw new IOException(L10n.Text("请输入完整的 12 位授权码"));
        code.Text=normalized;
        var request=await core.Call("inspectPairing",new(){["code"]=code.Text}) as JsonObject ?? new();var fingerprint=Str(request,"fingerprint");
        var confirm=new StackPanel{Spacing=10};confirm.Children.Add(new TextBlock{Text=Str(request,"kind")+"\n"+fingerprint+L10n.Text("\n仅当请求设备显示相同指纹时授权。"),IsTextSelectionEnabled=true,TextWrapping=TextWrapping.Wrap});
        if(await Dialog(L10n.Text("核对请求设备指纹"),confirm,L10n.Text("指纹一致，授权"))==ContentDialogResult.Primary)await core.Call("approvePairing",new(){["code"]=code.Text,["text"]=fingerprint,["passphrase"]=DeviceSecret.GetOrCreate()});
    }
    async Task WaitForAuthorization(StackPanel panel) {
        var notice=new TextBlock{TextWrapping=TextWrapping.Wrap};
        var image=AuthorizationScanner.CodeImage(Str(pairing!,"qrCode",$"cliper-pair-v1:{Str(pairing!,"code")}:{Str(pairing!,"fingerprint")}"));
        var label=new TextBlock{IsTextSelectionEnabled=true,TextWrapping=TextWrapping.Wrap,FontSize=22};
        var fingerprint=new TextBlock{IsTextSelectionEnabled=true,TextWrapping=TextWrapping.Wrap};
        panel.Children.Insert(0,new TextBlock{Text=L10n.Text("使用已授权设备扫描二维码，或在该设备输入下面的授权码。"),TextWrapping=TextWrapping.Wrap});
        panel.Children.Insert(1,image);panel.Children.Insert(2,label);panel.Children.Insert(3,fingerprint);panel.Children.Insert(4,notice);
        var dialog=new ContentDialog{Title=L10n.Text("等待设备授权"),Content=new ScrollViewer{Content=panel,MaxHeight=560},PrimaryButtonText=L10n.Text("检查授权结果"),SecondaryButtonText=L10n.Text("更换授权码"),CloseButtonText=L10n.Text("关闭"),XamlRoot=DialogRoot};
        using var stop=new CancellationTokenSource();bool checking=false;
        var cancel=new Button{Content=L10n.Text("取消请求")};panel.Children.Add(cancel);
        cancel.Click+=async(_,_)=>{
            if(checking)return;checking=true;
            try{await core.Call("cancelPairing");pairing=null;dialog.Hide();}
            catch(Exception e){notice.Text=L10n.Text(e.Message);}
            finally{checking=false;}
        };
        void UpdateCode() {
            var value=Str(pairing!,"code");image.Source=AuthorizationScanner.CodeImage(Str(pairing!,"qrCode",$"cliper-pair-v1:{value}:{Str(pairing!,"fingerprint")}")).Source;
            label.Text=string.Join("-",Enumerable.Range(0,3).Select(i=>value.Substring(i*4,4)));
            fingerprint.Text=L10n.Text("请在两台设备上核对指纹。授权码会过期。")+"\n"+Str(pairing!,"fingerprint");
            notice.Text=L10n.Text("授权码 5 分钟有效，批准后将自动连接。");
        }
        async Task Check(bool renew=false) {
            if(checking||stop.IsCancellationRequested)return;
            checking=true;dialog.IsPrimaryButtonEnabled=false;dialog.IsSecondaryButtonEnabled=false;
            try {
                var result=await core.Call(renew?"renewPairing":"completePairing",renew?null:new(){["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject ?? new();
                if(stop.IsCancellationRequested)return;
                if(Str(result,"state") is "connected" or "syncRequired") {pairing=null;dialog.Hide();return;}
                if(result["pairing"] is JsonObject next){pairing=next;UpdateCode();}
            } catch(Exception e){if(!stop.IsCancellationRequested)notice.Text=L10n.Text(e.Message);}
            finally {checking=false;dialog.IsPrimaryButtonEnabled=true;dialog.IsSecondaryButtonEnabled=true;}
        }
        async Task Monitor() {
            try {while(!stop.IsCancellationRequested){await Task.Delay(3000,stop.Token);await Check();}}
            catch(OperationCanceledException) { }
        }
        dialog.PrimaryButtonClick+=async(_,args)=>{args.Cancel=true;await Check();};
        dialog.SecondaryButtonClick+=async(_,args)=>{args.Cancel=true;await Check(true);};
        Task monitor=Task.CompletedTask;
        dialog.Opened+=(_,_)=>monitor=Monitor();dialog.Closed+=(_,_)=>stop.Cancel();
        UpdateCode();
        try{await dialog.ShowAsync();}finally{stop.Cancel();await monitor;}
        await Refresh();
    }
    async Task Devices() {
        var data=await core.Call("devices");var devices=data as JsonArray ?? (data as JsonObject)?["devices"] as JsonArray ?? new();
        var panel=new StackPanel{Spacing=8};var notice=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(notice); var revokePass=new PasswordBox{PlaceholderText=L10n.Text("原资料库口令（旧版凭据缺失时）")}; if(DeviceSecret.Read()==null)panel.Children.Add(revokePass);
        foreach(var device in devices.OfType<JsonObject>()) {
            panel.Children.Add(new TextBlock{Text=Str(device,"name",Str(device,"id"))});
            bool confirming=false;var cancelRevoke=new Button{Content=L10n.Text("取消撤销"),Visibility=Visibility.Collapsed};var revoke=new Button{Content=L10n.Text("撤销设备"),IsEnabled=device["current"]?.GetValue<bool>()!=true&&device["revoked"]?.GetValue<bool>()!=true};revoke.Click+=async (_,_)=>{revoke.IsEnabled=false;try{ if(!confirming){confirming=true;revoke.Content=L10n.Text("确认撤销：已解密内容无法远程抹除");cancelRevoke.Visibility=Visibility.Visible;revoke.IsEnabled=true;return;} var value=DeviceSecret.Read() ?? revokePass.Password;if(string.IsNullOrEmpty(value))throw new IOException(L10n.Text("请输入原资料库口令"));await core.Call("revoke",new(){["deviceId"]=Str(device,"id"),["passphrase"]=value});revokePass.Password="";cancelRevoke.Visibility=Visibility.Collapsed;}catch(Exception e){notice.Text=L10n.Text(e.Message);revoke.IsEnabled=true;}};cancelRevoke.Click+=(_,_)=>{confirming=false;revoke.Content=L10n.Text("撤销设备");cancelRevoke.Visibility=Visibility.Collapsed;revokePass.Password="";};panel.Children.Add(revoke);panel.Children.Add(cancelRevoke);
        }
        await Dialog(L10n.Text("设备"),panel,L10n.Text("完成"));await Refresh();
    }
}
