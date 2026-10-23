using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace ClipWeave;
public sealed class MainWindow : Window {
    readonly GoClient core=new();
    readonly StackPanel body=new(){Spacing=12,Margin=new Thickness(24)};
    readonly ScrollViewer viewport=new();
    readonly TextBlock state=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock error=new(){TextWrapping=TextWrapping.Wrap,Foreground=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed)};
    readonly ListView history=new();
    readonly TextBox search=new(){PlaceholderText=L10n.Text("搜索历史")};
    readonly TextBox groupFilter=new(){PlaceholderText=L10n.Text("筛选分组（留空显示全部）")};
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
    public MainWindow() {
        Title="Clipper"; viewport.Content=body; Content=viewport;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(660,850));
        refreshTimer=DispatcherQueue.CreateTimer(); refreshTimer.Interval=TimeSpan.FromSeconds(3); refreshTimer.Tick+=async (_,_)=>{if(!busy&&!closed)await Run(Refresh);}; refreshTimer.Start();
        Closed += async (_,_)=>{closed=true;refreshTimer.Stop();ClearPreviews();await core.DisposeAsync();};
        _=Run(Refresh);
    }
    async Task Run(Func<Task> operation) {
        if(busy||closed) return; busy=true; error.Text=""; viewport.IsEnabled=false;
        try { await operation(); } catch(Exception e) { if(e is BridgeException failure && failure.Payload!=null) { await SaveRecovery(failure.Payload);try {await Refresh();}catch{} } error.Text=L10n.Text(e.Message); }
        finally {viewport.IsEnabled=true;busy=false;}
    }
    Button Button(string title,Func<Task> action) { var button=new Button{Content=title}; button.Click+=async (_,_)=>await Run(action);return button; }
    async Task Action(string name,JsonObject? args=null) {await core.Call(name,args);if(name=="lock")ClearPreviews();await Refresh();}
    static string Str(JsonObject value,string key,string fallback="")=> key=="status" ? L10n.Text(value[key]?.ToString() ?? fallback) : value[key]?.ToString() ?? fallback;
    bool sessionRemembered;
    bool AccountReady => status["unlocked"]?.GetValue<bool>()==true && (status["config"] as JsonObject)?["connected"]?.GetValue<bool>()==true;
    async Task Refresh() {
        await core.Call("requireAccount");
        var wasUnlocked=AccountReady;
        status=await core.Call("status") as JsonObject ?? new();
        if(!triedAutoUnlock) {triedAutoUnlock=true;if(status["exists"]?.GetValue<bool>()==true&&status["unlocked"]?.GetValue<bool>()!=true&&DeviceSecret.Read() is string pass) {await core.Call("unlock",new(){["passphrase"]=pass});status=await core.Call("status") as JsonObject ?? new();}}
        if(status["unlocked"]?.GetValue<bool>()==true) sessionRemembered=AccountReady;
        if(AccountReady) await CaptureClipboard();
        clips=AccountReady ? (await core.Call("list") as JsonArray ?? new()).OfType<JsonObject>().ToList() : new();
        if(body.Children.Count==0||wasUnlocked!=AccountReady) Render();
        else { state.Text=L10n.Template("{0} · 待同步 {1}", Str(status,"status",L10n.Text("未连接")), Str(status,"pending","0")); RenderHistory(); }
    }
    void Render() {
        body.Children.Clear(); body.Children.Add(new TextBlock{Text="Clipper",FontSize=28});
        state.Text=L10n.Template("{0} · 待同步 {1}", Str(status,"status",L10n.Text("未连接")), Str(status,"pending","0"));
        body.Children.Add(state);body.Children.Add(error);
        if(!AccountReady) {
            if((status["config"] as JsonObject)?["authenticated"]?.GetValue<bool>()==true && !sessionRemembered) {
                body.Children.Add(new TextBlock{Text=L10n.Text("首次云端同步完成后即可使用。本机仅保留加密缓存。"),TextWrapping=TextWrapping.Wrap});
                body.Children.Add(Button(L10n.Text("重试云端同步"),()=>Action("sync")));
                return;
            }
            if(sessionRemembered) body.Children.Add(Button(L10n.Text("使用设备安全凭据解锁"),async()=>{var value=DeviceSecret.Read() ?? throw new IOException(L10n.Text("没有已保存的设备凭据"));await Action("unlock",new(){["passphrase"]=value});}));
            body.Children.Add(new TextBlock{Text=L10n.Text("登录后，安全保存并同步你的剪贴板历史。"),TextWrapping=TextWrapping.Wrap,FontSize=18});
            body.Children.Add(Button(L10n.Text("登录 Clipper"),Connect));
            return;
        }
        var text=new TextBox{PlaceholderText=L10n.Text("添加文字"),AcceptsReturn=true,MaxHeight=120,TextWrapping=TextWrapping.Wrap};body.Children.Add(text);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};body.Children.Add(actions);
        actions.Children.Add(Button(L10n.Text("添加"),async ()=>{await Action("addText",new(){["text"]=text.Text});text.Text="";}));
        actions.Children.Add(Button(L10n.Text("读取剪贴板"),async ()=>{
            var content=Clipboard.GetContent();if(content.Contains(StandardDataFormats.Text)) { lastCaptured=await content.GetTextAsync();await Action("addText",new(){["text"]=lastCaptured,["html"]=content.Contains(StandardDataFormats.Html)?HtmlFormatHelper.GetStaticFragment(await content.GetHtmlFormatAsync()):""}); }
        }));
        actions.Children.Add(Button(L10n.Text("导入文件"),ImportFile)); actions.Children.Add(Button(L10n.Text("导入文件夹"),ImportFolder));
        search.TextChanged-=FilterChanged; search.TextChanged+=FilterChanged;body.Children.Add(search);
        groupFilter.TextChanged-=FilterChanged;groupFilter.TextChanged+=FilterChanged;body.Children.Add(groupFilter);
        body.Children.Add(history);RenderHistory();
        var sync=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};body.Children.Add(sync);
        sync.Children.Add(Button(L10n.Text("同步"),()=>Action("sync")));sync.Children.Add(Button(L10n.Text("账户"),Connect));sync.Children.Add(Button(L10n.Text("设备"),Devices));sync.Children.Add(Button(L10n.Text("设置"),Settings));sync.Children.Add(Button(L10n.Text("锁定"),()=>Action("lock")));
        body.Children.Add(Button(L10n.Text("授权与恢复"),Authorization));
        body.Children.Add(Button(L10n.Text("断开账户连接"),()=>Action("disconnect")));
    }
    void FilterChanged(object sender,TextChangedEventArgs args)=>RenderHistory();
    void RenderHistory() {
        history.Items.Clear();
        foreach(var clip in clips.Where(c=>(string.IsNullOrWhiteSpace(search.Text)||string.Join(" ",new[]{Str(c,"title"),Str(c,"text"),Str(c,"name"),Str(c,"group")}).Contains(search.Text,StringComparison.OrdinalIgnoreCase))&&(groupFilter.Text.Length==0||Str(c,"group")==groupFilter.Text))) {
            var card=new StackPanel{Spacing=6,Margin=new Thickness(0,8,0,8)};
            card.Children.Add(new TextBlock{Text=Str(clip,"title",Str(clip,"text",Str(clip,"fileName",L10n.Text("内容")))),TextWrapping=TextWrapping.Wrap,MaxLines=3});
            card.Children.Add(new TextBlock{Text=$"{Str(clip,"type")} · {Str(clip,"group")}",Opacity=.65});
            var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};card.Children.Add(controls);
            JsonObject Args()=>new(){["clipId"]=Str(clip,"id")};
            controls.Children.Add(Button(L10n.Text("复制"),async ()=>{
                var value=await core.Call("preview",Args()) as JsonObject ?? new();
                if(value["text"]==null) throw new IOException(L10n.Text("附件请使用导出按钮"));
                var data=new DataPackage();lastCaptured=Str(value,"text");data.SetText(lastCaptured);if(value["html"]!=null)data.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Str(value,"html")));Clipboard.SetContent(data);
            }));
            controls.Children.Add(Button(L10n.Text("预览"),async ()=>{
                var value=await core.Call("preview",Args()) as JsonObject ?? new();
                if(value["text"]!=null) await Dialog(L10n.Text("预览"),new TextBox{Text=Str(value,"text"),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=400},L10n.Text("关闭"));
                else {
                    var extension=Path.GetExtension(Str(clip,"name")).ToLowerInvariant();
                    if(!new[]{".png",".jpg",".jpeg",".gif",".webp",".bmp",".tif",".tiff",".pdf",".txt",".md",".json",".log",".mp3",".m4a",".wav",".mp4",".mov"}.Contains(extension)) throw new IOException(L10n.Text("此格式请先导出，再选择打开方式"));
                    var directory=Path.Combine(Path.GetTempPath(),"Clipper","Previews",Guid.NewGuid().ToString());Directory.CreateDirectory(directory);previewDirectories.Add(directory);
                    var name=Path.GetFileName(Str(clip,"name","Clipper.bin"));var file=Path.Combine(directory,name);
                    var args=Args();args["path"]=file;await core.Call("export",args);
                    if(!await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(file))) throw new IOException(L10n.Text("此文件没有可用的预览程序"));
                }
            }));
            controls.Children.Add(Button(L10n.Text("导出"),async ()=>{
                var picker=new FolderPicker();picker.FileTypeFilter.Add("*");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(this));
                var folder=await picker.PickSingleFolderAsync();if(folder==null)return;
                var name=Path.GetFileName(Str(clip,"name",Str(clip,"title","ClipWeave.txt")));if(string.IsNullOrWhiteSpace(name))name="ClipWeave.txt";
                var args=Args();args["path"]=Path.Combine(folder.Path,name);await core.Call("export",args);
            }));
            var favourite=clip["favourite"]?.GetValue<bool>()==true;
            controls.Children.Add(Button(favourite?L10n.Text("取消收藏"):L10n.Text("收藏"),async ()=>{var args=Args();args["value"]=!favourite;await Action("favourite",args);}));
            controls.Children.Add(Button(L10n.Text("分组"),async ()=>{
                var input=new TextBox{Text=Str(clip,"group"),PlaceholderText=L10n.Text("分组名称（留空移除）")};
                if(await Dialog(L10n.Text("分组"),input,L10n.Text("保存"))==ContentDialogResult.Primary) {var args=Args();args["name"]=input.Text;await Action("group",args);}
            }));
            controls.Children.Add(Button(L10n.Text("删除"),async ()=>{if(await Dialog(L10n.Text("删除此内容？"),new TextBlock{Text=L10n.Text("删除会同步至其他设备。")},L10n.Text("删除"))==ContentDialogResult.Primary)await Action("delete",Args());}));
            history.Items.Add(card);
        }
    }
    Task<ContentDialogResult> Dialog(string title,UIElement content,string primary)=>new ContentDialog{Title=title,Content=content,PrimaryButtonText=primary,CloseButtonText=L10n.Text("取消"),XamlRoot=body.XamlRoot}.ShowAsync().AsTask();
    async Task SaveRecovery(JsonObject? result) {
        var generated=result?["recovery"]?.ToString();if(!string.IsNullOrEmpty(generated)&&pendingRecovery.Length==0)pendingRecovery=generated;var key=pendingRecovery;if(key.Length==0)return;lastCaptured=key;
        var picker=new FileSavePicker{SuggestedFileName="ClipWeave-Recovery"};picker.FileTypeChoices.Add("Recovery JSON",new List<string>{".json"});InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(this));var file=await picker.PickSaveFileAsync();
        if(file!=null){await Windows.Storage.FileIO.WriteTextAsync(file,key);pendingRecovery="";}

    }
    async Task ImportFile() {
        var picker=new FileOpenPicker();picker.FileTypeFilter.Add("*");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(this));var file=await picker.PickSingleFileAsync();
        if(file!=null)await Action("importFile",new(){["path"]=file.Path});
    }
    async Task ImportFolder() {
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(this));var folder=await picker.PickSingleFolderAsync();
        if(folder!=null)await Action("importFolder",new(){["path"]=folder.Path});
    }
    void ClearPreviews() {
        foreach(var directory in previewDirectories.ToArray()) {
            try { Directory.Delete(directory,true);previewDirectories.Remove(directory); }
            catch(IOException) { /* A native viewer may still hold its file handle; retry on close. */ }
            catch(UnauthorizedAccessException) { }
        }
    }
    async Task CaptureClipboard() {
        var config=status["config"] as JsonObject ?? new();
        if(config["captureEnabled"]?.GetValue<bool>()!=true)return;
        var owner=GetClipboardOwner();if(owner==IntPtr.Zero)return;
        GetWindowThreadProcessId(owner,out var processId);
        if(processId==(uint)Environment.ProcessId)return;
        string source;
        try { source=Process.GetProcessById((int)processId).ProcessName+".exe"; } catch {return;}
        if(Str(config,"excludedApps").Split('\n').Any(value=>value.Trim().Equals(source,StringComparison.OrdinalIgnoreCase)))return;
        string text;string html="";
        try {
            var content=Clipboard.GetContent();if(!content.Contains(StandardDataFormats.Text))return;
            text=await content.GetTextAsync();if(content.Contains(StandardDataFormats.Html))html=HtmlFormatHelper.GetStaticFragment(await content.GetHtmlFormatAsync());
        } catch(COMException) {return;}
        if(string.IsNullOrEmpty(text)||text==lastCaptured)return;
        if(System.Text.Encoding.UTF8.GetByteCount(text)+System.Text.Encoding.UTF8.GetByteCount(html)>32*1024)return;
        await core.Call("addText",new(){["text"]=text,["html"]=html});lastCaptured=text;
    }
    async Task Settings() {
        var config=status["config"] as JsonObject ?? new();
        var capture=new CheckBox{Content=L10n.Text("自动收集剪贴板文字"),IsChecked=config["captureEnabled"]?.GetValue<bool>()==true};
        var excluded=new TextBox{Header=L10n.Text("忽略来源（每行一个程序名）"),Text=Str(config,"excludedApps"),AcceptsReturn=true,MaxHeight=140,TextWrapping=TextWrapping.Wrap};
        var retention=new ComboBox{Header=L10n.Text("自动清理未收藏内容")};foreach(var days in new[]{0,7,30,90})retention.Items.Add(new ComboBoxItem{Content=days==0?L10n.Text("永久保留"):L10n.Template("{0} 天", days),Tag=days});
        var current=config["retentionDays"]?.GetValue<int>() ?? 0;retention.SelectedItem=retention.Items.Cast<ComboBoxItem>().First(item=>(int)item.Tag==current);
        var panel=new StackPanel{Spacing=12};panel.Children.Add(capture);panel.Children.Add(excluded);panel.Children.Add(retention);
        if(await Dialog(L10n.Text("本机设置"),panel,L10n.Text("保存"))==ContentDialogResult.Primary) await Action("config",new(){["values"]=new JsonObject{["captureEnabled"]=capture.IsChecked==true,["excludedApps"]=excluded.Text,["retentionDays"]=(int)((ComboBoxItem)retention.SelectedItem).Tag}});
    }
    async Task Connect() {
        var config=status["config"] as JsonObject ?? new();
        var endpoint=new TextBox{PlaceholderText=L10n.Text("服务地址 https://…"),Text="https://api.cliper.dev"};var email=new TextBox{PlaceholderText=L10n.Text("邮箱"),Text=Str(config,"email")};
        var code=new TextBox{PlaceholderText=L10n.Text("邮箱验证码")};var secret=new PasswordBox{PlaceholderText=L10n.Text("账户资料库口令或恢复密钥")};var recovery=new CheckBox{Content=L10n.Text("使用恢复密钥")};var notice=new TextBlock{TextWrapping=TextWrapping.Wrap};
        var send=new Button{Content=L10n.Text("发送验证码")};send.Click+=async (_,_)=>{send.IsEnabled=false;try{var result=await core.Call("requestCode",new(){["endpoint"]=endpoint.Text,["email"]=email.Text}) as JsonObject;var local=result?["localCode"]?.ToString();if(!string.IsNullOrEmpty(local)){code.Text=local;notice.Text=L10n.Text("本地开发验证码已填入。");}else notice.Text=L10n.Text("已请求验证码，请查看邮箱。");}catch(Exception e){notice.Text=L10n.Text(e.Message);}finally{send.IsEnabled=true;}};
        var password=new PasswordBox{Header=L10n.Text("账户密码")};var confirmPassword=new PasswordBox{Header=L10n.Text("确认新密码")};
        var passwordMode=new CheckBox{Content=L10n.Text("使用密码登录"),IsChecked=true};var resetting=false;var resetPassword=new Button{Content=L10n.Text("忘记密码")};
        var panel=new StackPanel{Spacing=8};foreach(var view in new UIElement[]{new TextBlock{Text=L10n.Text("登录并同步云端历史，本机仅保留缓存。")},email,passwordMode,password,resetPassword,confirmPassword,send,notice,code})panel.Children.Add(view);
        void UpdateLoginMode() {
            var reset=resetting;var usePassword=passwordMode.IsChecked==true || reset;
            password.Visibility=usePassword?Visibility.Visible:Visibility.Collapsed;
            confirmPassword.Visibility=reset?Visibility.Visible:Visibility.Collapsed;
            code.Visibility=send.Visibility=(!usePassword || reset)?Visibility.Visible:Visibility.Collapsed;
        }
        passwordMode.Checked+=(_,_)=>UpdateLoginMode();passwordMode.Unchecked+=(_,_)=>UpdateLoginMode();
        resetPassword.Click+=(_,_)=>{resetting=true;UpdateLoginMode();};UpdateLoginMode();
        if(await Dialog(L10n.Text("登录 Clipper"),panel,L10n.Text("登录"))!=ContentDialogResult.Primary)return;
        if(resetting && (password.Password.Length<8 || password.Password.Length>128 || password.Password!=confirmPassword.Password || code.Text.Length!=6))throw new Exception(L10n.Text("请填写验证码和两次一致的 8–128 字符新密码。"));
        var usePassword=passwordMode.IsChecked==true || resetting;
        var result=await core.Call(usePassword?"passwordConnect":"autoConnect",new(){["password"]=usePassword?password.Password:"",["endpoint"]=endpoint.Text,["email"]=email.Text,["code"]=usePassword&&!resetting?"":code.Text,["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject;
        if(result?["passwordSetupRequired"]?.GetValue<bool>()==true) {
            var ticket=Str(result,"verificationToken");var first=new PasswordBox{Header=L10n.Text("设置账户密码（8–128 个字符）")};var second=new PasswordBox{Header=L10n.Text("确认密码")};var message=new TextBlock{Text=L10n.Text("邮箱已验证，请设置密码后继续。"),TextWrapping=TextWrapping.Wrap};var setup=new StackPanel{Spacing=16};setup.Children.Add(message);setup.Children.Add(first);setup.Children.Add(second);
            var setupDialog=new ContentDialog{Title=L10n.Text("设置账户密码"),Content=setup,PrimaryButtonText=L10n.Text("保存密码并登录"),CloseButtonText=L10n.Text("取消"),XamlRoot=body.XamlRoot};
            setupDialog.PrimaryButtonClick+=(_,args)=>{if(first.Password.Length<8||first.Password.Length>128||first.Password!=second.Password){message.Text=L10n.Text("请输入两次一致的 8–128 字符密码。");args.Cancel=true;}};
            if(await setupDialog.ShowAsync()!=ContentDialogResult.Primary)return;
            result=await core.Call("passwordConnect",new(){["endpoint"]=endpoint.Text,["email"]=email.Text,["code"]=ticket,["password"]=first.Password,["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject;first.Password="";second.Password="";
        }
        password.Password="";confirmPassword.Password="";pairing=result?["pairing"] as JsonObject;secret.Password="";await SaveRecovery(result);await Refresh();if(pairing!=null)await Authorization();
    }
    async Task Authorization() {
        var panel=new StackPanel{Spacing=10};
        var code=new TextBox{PlaceholderText=L10n.Text("设备 / 浏览器授权码")};panel.Children.Add(code);
        var import=new Button{Content=L10n.Text("导入恢复文件…")};import.Click+=async(_,_)=>{try {var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".json");InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(this));var file=await picker.PickSingleFileAsync();if(file!=null){var text=await Windows.Storage.FileIO.ReadTextAsync(file);await core.Call("importRecovery",new(){["text"]=text,["passphrase"]=DeviceSecret.GetOrCreate()});await Refresh();}}catch(Exception e){error.Text=L10n.Text(e.Message);}};panel.Children.Add(import);
        var export=new Button{Content=L10n.Text("导出恢复文件…")};export.Click+=async(_,_)=>{try{await SaveRecovery(await core.Call("exportRecovery",new(){["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject);}catch(Exception e){error.Text=L10n.Text(e.Message);}};panel.Children.Add(export);
        if(pairing!=null) {
            panel.Children.Add(DeviceIdentityCard(pairing));
            panel.Children.Add(new TextBlock{Text=L10n.Text("在已授权设备输入：")+Str(pairing,"code")+"\n"+Str(pairing,"fingerprint"),IsTextSelectionEnabled=true,TextWrapping=TextWrapping.Wrap});
            var cancel=new Button{Content=L10n.Text("取消请求")};cancel.Click+=async(_,_)=>{try{await core.Call("cancelPairing");pairing=null;}catch(Exception e){error.Text=L10n.Text(e.Message);}};panel.Children.Add(cancel);
            if(await Dialog(L10n.Text("等待设备授权"),panel,L10n.Text("检查授权结果"))==ContentDialogResult.Primary) {var result=await core.Call("completePairing",new(){["passphrase"]=DeviceSecret.GetOrCreate()}) as JsonObject;if((Str(result??new(),"state")=="connected" || Str(result??new(),"state")=="syncRequired"))pairing=null;await Refresh();}
            return;
        }
        if(status["unlocked"]?.GetValue<bool>()==true) {
            var data=await core.Call("pairingGrants");var grants=data as JsonArray ?? (data as JsonObject)?["requests"] as JsonArray ?? new();
            foreach(var grant in grants.OfType<JsonObject>()) {var id=Str(grant,"id");var cancel=new Button{Content=L10n.Text("取消浏览器授权 ")+id};cancel.Click+=async(_,_)=>{try{await core.Call("cancelGrant",new(){["deviceId"]=id});cancel.IsEnabled=false;}catch(Exception e){error.Text=L10n.Text(e.Message);}};panel.Children.Add(cancel);}
        }
        if(await Dialog(L10n.Text("授权与恢复"),panel,L10n.Text("检查请求"))!=ContentDialogResult.Primary||string.IsNullOrWhiteSpace(code.Text))return;
        var request=await core.Call("inspectPairing",new(){["code"]=code.Text}) as JsonObject ?? new();var fingerprint=Str(request,"fingerprint");
        var confirm=new StackPanel{Spacing=10};confirm.Children.Add(DeviceIdentityCard(request));confirm.Children.Add(new TextBlock{Text=Str(request,"kind")+"\n"+fingerprint+L10n.Text("\n仅当请求设备显示相同指纹时授权。"),IsTextSelectionEnabled=true,TextWrapping=TextWrapping.Wrap});
        if(await Dialog(L10n.Text("核对请求设备指纹"),confirm,L10n.Text("指纹一致，授权"))==ContentDialogResult.Primary)await core.Call("approvePairing",new(){["code"]=code.Text,["text"]=fingerprint,["passphrase"]=DeviceSecret.GetOrCreate()});
    }
    static FrameworkElement DeviceIdentityCard(JsonObject value,bool authorized=false) {
        var info=value["deviceInfo"] as JsonObject??value;var platform=Str(info,"platform");
        var symbol=platform switch {"iphone"=>"\uE8EA","ipad"=>"\uE70A","mac"=>"\uE770","windows"=>"\uE782","android"=>"\uE8EA","web"=>"\uE774",_=>"\uE7F8"};
        var label=platform switch {"iphone"=>"iPhone","ipad"=>"iPad","mac"=>"Mac","windows"=>"Windows","android"=>"Android","web"=>L10n.Text("浏览器"),_=>L10n.Text("设备")};
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};row.Children.Add(new FontIcon{Glyph=symbol,FontSize=26});
        var text=new StackPanel{Spacing=4};text.Children.Add(new TextBlock{Text=Str(info,"name",L10n.Text("设备")),TextWrapping=TextWrapping.Wrap});
        text.Children.Add(new TextBlock{Text=string.Join(" · ",new[]{label,Str(info,"model"),Str(info,"osVersion")}.Where(x=>!string.IsNullOrEmpty(x))),TextWrapping=TextWrapping.Wrap,Opacity=.7});
        var raw=value["approvedAt"]??value["created"];
        if(long.TryParse(raw?.ToString(),out var ms)&&ms>0) text.Children.Add(new TextBlock{Text=L10n.Text(authorized?"授权时间":"请求时间")+" · "+DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g"),Opacity=.7});
        row.Children.Add(text);return row;
    }
    async Task Devices() {
        var data=await core.Call("devices");var devices=data as JsonArray ?? (data as JsonObject)?["devices"] as JsonArray ?? new();
        var panel=new StackPanel{Spacing=8}; var revokePass=new PasswordBox{PlaceholderText=L10n.Text("原资料库口令（旧版凭据缺失时）")}; if(DeviceSecret.Read()==null)panel.Children.Add(revokePass);
        foreach(var device in devices.OfType<JsonObject>()) {
            panel.Children.Add(DeviceIdentityCard(device,true));
            bool confirming=false;var cancelRevoke=new Button{Content=L10n.Text("取消撤销"),Visibility=Visibility.Collapsed};var revoke=new Button{Content=L10n.Text("撤销设备"),IsEnabled=device["current"]?.GetValue<bool>()!=true&&device["revoked"]?.GetValue<bool>()!=true};revoke.Click+=async (_,_)=>{revoke.IsEnabled=false;try{ if(!confirming){confirming=true;revoke.Content=L10n.Text("确认撤销：已解密内容无法远程抹除");cancelRevoke.Visibility=Visibility.Visible;revoke.IsEnabled=true;return;} var value=DeviceSecret.Read() ?? revokePass.Password;if(string.IsNullOrEmpty(value))throw new IOException(L10n.Text("请输入原资料库口令"));await core.Call("revoke",new(){["deviceId"]=Str(device,"id"),["passphrase"]=value});revokePass.Password="";cancelRevoke.Visibility=Visibility.Collapsed;}catch(Exception e){error.Text=L10n.Text(e.Message);revoke.IsEnabled=true;}};cancelRevoke.Click+=(_,_)=>{confirming=false;revoke.Content=L10n.Text("撤销设备");cancelRevoke.Visibility=Visibility.Collapsed;revokePass.Password="";};panel.Children.Add(revoke);panel.Children.Add(cancelRevoke);
        }
        await Dialog(L10n.Text("设备"),panel,L10n.Text("完成"));await Refresh();
    }
}
