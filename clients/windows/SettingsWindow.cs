using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using System.Text.Json.Nodes;

namespace ClipWeave;
sealed class SettingsWindow:Window {
    readonly MainWindow owner;
    readonly Grid root=new(){Background=Palette.Soft,RequestedTheme=ElementTheme.Light};
    readonly Grid navigation=new(){Background=Palette.White,Padding=new Thickness(18,10,18,8)};
    readonly ContentControl pageHost=new(){ContentTransitions=new Microsoft.UI.Xaml.Media.Animation.TransitionCollection(),HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch};
    readonly TextBlock notice=Palette.Text("");
    readonly List<Button> navigationButtons=[];
    readonly List<ScrollViewer> pages=[];
    readonly List<Control> accountControls=[];
    readonly List<System.Action> updateToggles=[];
    readonly TextBlock connection=Palette.Text(""),pending=Palette.Text(""),email=Palette.Text(""),endpoint=Palette.Text("");
    readonly StackPanel deviceList=new(){Spacing=12},eventList=new(){Spacing=10};
    readonly TextBlock accountMemberType=Palette.Text(""),accountMemberState=Palette.Text(""),accountMemberExpiry=Palette.Text("");
    readonly TextBlock membershipStatus=Palette.Text(""),membershipExpiry=Palette.Text(""),membershipSource=Palette.Text("");
    readonly TextBlock cleanupResult=Palette.Text("");
    bool busy,closed,catalogueUnavailable,catalogueLoaded;
    readonly HashSet<string> availablePlans=new();
    readonly List<(Button button,Func<string> plan)> purchaseButtons=[];
    readonly TextBlock membershipDevices=Palette.Text(""),membershipStorage=Palette.Text("");
    string membershipOrigin="";
    readonly Dictionary<string,decimal> membershipPrices=new();
    Action? redrawMembershipPrices;
    JsonObject Config=>owner.Status["config"] as JsonObject??new();

    public SettingsWindow(MainWindow owner){
        UiDiagnostics.Stage("settings-constructor-entered");this.owner=owner;Title=L10n.Text("设置")+" — Clipper";
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.Children.Add(navigation);Grid.SetRow(pageHost,1);root.Children.Add(pageHost);
        notice.Margin=new Thickness(28,8,28,16);notice.Foreground=Palette.Muted;Grid.SetRow(notice,2);root.Children.Add(notice);
        Content=root;WindowAppearance.Apply(this);UiDiagnostics.Stage("before-settings-build");Build();UiDiagnostics.Stage("after-settings-build");Select(0);UpdateState();
        WindowAppearance.SizeCentered(this,900,740,owner);
        owner.StateChanged+=UpdateState;
        Closed+=(_,_)=>{closed=true;owner.StateChanged-=UpdateState;owner.SettingsClosed();};
        root.KeyDown+=(_,e)=>{if(e.Key==Windows.System.VirtualKey.Escape){Close();e.Handled=true;}};
    }
    StackPanel Page(string title){var p=new StackPanel{Spacing=22,Padding=new Thickness(26,24,26,20),Width=800,MaxWidth=800,HorizontalAlignment=HorizontalAlignment.Center};p.Children.Add(Palette.Text(L10n.Text(title),23,true));return p;}
    StackPanel Section(StackPanel page,string title,string detail=""){
        var group=new StackPanel{Spacing=12};group.Children.Add(Palette.Text(L10n.Text(title),13,true));
        var content=new StackPanel{Spacing=15};group.Children.Add(new Border{Background=Palette.White,CornerRadius=new CornerRadius(12),Padding=new Thickness(18),Child=content});page.Children.Add(group);
        if(detail.Length>0)Help(content,detail);return content;
    }
    static void Help(StackPanel parent,string key){var text=Palette.Text(L10n.Text(key),12);text.Foreground=Palette.Muted;parent.Children.Add(text);}
    static void Row(StackPanel parent,string key,TextBlock value){var row=new Grid{ColumnSpacing=18};row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.Children.Add(Palette.Text(L10n.Text(key)));value.HorizontalAlignment=HorizontalAlignment.Right;value.TextWrapping=TextWrapping.Wrap;Grid.SetColumn(value,1);row.Children.Add(value);parent.Children.Add(row);}
    void Tab(string key,string title,string glyph,StackPanel page){
        var index=pages.Count;var scroll=new ScrollViewer{Content=page,HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};scroll.SizeChanged+=(_,e)=>page.Width=Math.Max(1,Math.Min(800,e.NewSize.Width));pages.Add(scroll);
        navigation.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        var label=new StackPanel{Spacing=6,HorizontalAlignment=HorizontalAlignment.Center};label.Children.Add(new FontIcon{Glyph=glyph,FontSize=19});var text=Palette.Text(L10n.Text(title),11);text.TextAlignment=TextAlignment.Center;text.MaxLines=2;label.Children.Add(text);
        var button=new Button{Content=label,Background=Palette.White,BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(9),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Center,MinHeight=66,Padding=new Thickness(6)};
        AutomationProperties.SetAutomationId(button,"settings-tab-"+key);AutomationProperties.SetName(button,L10n.Text(title));Grid.SetColumn(button,index);navigation.Children.Add(button);navigationButtons.Add(button);button.Click+=(_,_)=>Select(index);
    }
    void Select(int index){pageHost.Content=pages[index];for(var i=0;i<navigationButtons.Count;i++){navigationButtons[i].Background=i==index?Palette.Soft:Palette.White;navigationButtons[i].Foreground=i==index?Palette.Blue:Palette.Muted;}
        if(index==1&&owner.AccountReady){_=Run(async()=>{try{await LoadMembership(true);}catch(Exception e){notice.Text=L10n.Error(e.Message);}await LoadRemote();});}if(index==3&&owner.AccountReady)_=Run(()=>LoadMembership(false));
    }
    Button Button(string title,Func<Task> action,bool account=false,string? id=null){
        var b=new Button{Content=L10n.Text(title),CornerRadius=new CornerRadius(8),Background=Palette.White,Foreground=Palette.Ink,BorderBrush=Palette.Border,BorderThickness=new Thickness(1),Padding=new Thickness(12,8,12,8)};if(id!=null)AutomationProperties.SetAutomationId(b,id);
        if(account)accountControls.Add(b);
        b.Click+=async(_,_)=>{if(busy)return;b.IsEnabled=false;try{await Run(action);}finally{if(!closed)b.IsEnabled=!account||owner.AccountReady;}};return b;
    }
    async Task Run(Func<Task> action){if(busy||closed)return;busy=true;UpdateState();notice.Text="";try{await action();}catch(Exception error){if(!closed)notice.Text=L10n.Error(error.Message);}finally{busy=false;if(!closed)UpdateState();}}
    void Toggle(StackPanel parent,string title,bool value,Func<bool,Task> save,bool account=false,Func<bool>? read=null){
        var toggle=new ToggleSwitch{Header=L10n.Text(title),IsOn=value};if(account)accountControls.Add(toggle);
        var reverting=false;if(read!=null)updateToggles.Add(()=>{var value=read();if(toggle.IsOn==value)return;reverting=true;toggle.IsOn=value;reverting=false;});
        toggle.Toggled+=async(_,_)=>{if(reverting||closed)return;var next=toggle.IsOn;if(busy){reverting=true;toggle.IsOn=!next;reverting=false;return;}toggle.IsEnabled=false;await Run(async()=>{try{await save(next);}catch{reverting=true;toggle.IsOn=!next;reverting=false;throw;}});if(!closed)toggle.IsEnabled=!account||owner.AccountReady;};parent.Children.Add(toggle);
    }
    void Setting(StackPanel parent,string title,string key)=>Toggle(parent,title,SettingsValues.Boolean(Config,key),on=>owner.SaveConfig(new(){[key]=on}),true,()=>SettingsValues.Boolean(Config,key));
    void UpdateState(){
        if(closed)return;
        connection.Text=L10n.ServiceStatus(owner.Status["status"]?.ToString()??"等待云端连接");pending.Text=L10n.Template("{0} 条",owner.Status["pending"]?.ToString()??"0");email.Text=Config["email"]?.ToString()??L10n.Text("未连接");endpoint.Text=Config["endpoint"]?.ToString()??"https://api.cliper.dev";
        foreach(var control in accountControls)control.IsEnabled=owner.AccountReady&&!busy;
        if(!busy)foreach(var update in updateToggles)update();
    }
    void Build(){
        var general=Page("通用");var languageGroup=Section(general,"语言");var language=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch};foreach(var title in new[]{L10n.Text("跟随系统"),"简体中文","English"})language.Items.Add(title);language.SelectedIndex=owner.Preferences.Language=="en"?2:owner.Preferences.Language=="zh-Hans"?1:0;AutomationProperties.SetAutomationId(language,"settings-language");language.SelectionChanged+=(_,_)=>{if(language.SelectedIndex>=0)owner.ChangeLanguage(new[]{"system","zh-Hans","en"}[language.SelectedIndex]);};languageGroup.Children.Add(language);Help(languageGroup,"语言切换立即生效；跟随系统时，其他语言默认使用英文。");Toggle(languageGroup,"错误语音播报",owner.Preferences.ErrorSpeech,value=>{owner.Preferences.ErrorSpeech=value;owner.Preferences.Save();return Task.CompletedTask;});var toolbar=Section(general,"工具栏与菜单栏");
        Toggle(toolbar,"固定工具栏",owner.Preferences.Pinned,on=>{owner.Preferences.Pinned=on;owner.Preferences.Save();return Task.CompletedTask;});Help(toolbar,"固定后，点击其他应用不会收起工具栏。打开设置时仍会自动收起。");
        Toggle(toolbar,"显示托盘图标",owner.Preferences.ShowTray,on=>{if(!on&&!owner.Shell.HotkeyAvailable)throw new IOException(L10n.Text("快捷键已被占用，请保留托盘图标。"));owner.Preferences.ShowTray=on;owner.Preferences.Save();owner.Shell.SetTrayVisible(on);return Task.CompletedTask;});Help(toolbar,"隐藏后仍可按 Ctrl+Shift+V 或重新打开 Clipper 唤出工具栏。");
        toolbar.Children.Add(Palette.Text(L10n.Text("面板高度")));var height=new Slider{Minimum=400,Maximum=760,StepFrequency=10,Value=Math.Clamp(owner.Preferences.PanelHeight,400,760)};AutomationProperties.SetAutomationId(height,"settings-height");var heightText=Palette.Text($"{height.Value:0} DIP");height.ValueChanged+=(_,e)=>{owner.SetPanelHeight(e.NewValue);heightText.Text=$"{e.NewValue:0} DIP";};toolbar.Children.Add(height);toolbar.Children.Add(heightText);Help(toolbar,"也可拖动面板顶部调整高度，始终固定在任务栏上方。");
        var capture=Section(general,"采集");Setting(capture,"自动记录剪贴板","captureEnabled");Setting(capture,"同时记录复制的文件和文件夹","captureFiles");Help(capture,"解锁后每 2 秒检查系统剪贴板。密码管理器标记的隐私内容、应用自己的秘密输入和下列来源不会采集。");
        var excluded=Section(general,"不记录这些应用");var input=new TextBox{AcceptsReturn=true,Height=95,Text=Config["excludedApps"]?.ToString()??"",FontFamily=new Microsoft.UI.Xaml.Media.FontFamily("Consolas")};accountControls.Add(input);excluded.Children.Add(input);Help(excluded,"每行一个 Windows 进程名，例如 1Password.exe、Bitwarden.exe。");excluded.Children.Add(Button("保存排除规则",()=>owner.SaveConfig(new(){["excludedApps"]=input.Text}),true));
        var habits=Section(general,"使用习惯");int[] days=[0,30,90,7];var retention=new ComboBox{Header=L10n.Text("保留历史"),HorizontalAlignment=HorizontalAlignment.Stretch};foreach(var day in days)retention.Items.Add(day==0?L10n.Text("永久保留"):L10n.Template("{0} 天",day));retention.SelectedIndex=Math.Max(0,Array.IndexOf(days,SettingsValues.RetentionDays(Config)));accountControls.Add(retention);habits.Children.Add(retention);
        habits.Children.Add(Button("保存保留期限",async()=>{var day=days[Math.Max(0,retention.SelectedIndex)];if(day>0&&await owner.Dialog(L10n.Text("更改保留期限"),Palette.Text(L10n.Text("收藏会保留；未收藏的过期内容将删除并同步至其他设备，无法在 App 内撤销。")),L10n.Text("保存并允许清理旧内容"))!=ContentDialogResult.Primary)return;await owner.SaveConfig(new(){["retentionDays"]=day});notice.Text=L10n.Text("已更新");},true));
        Setting(habits,"复制后自动粘贴","directPaste");Help(habits,"默认粘贴到刚才的应用；目标应用权限较高时，请按 Ctrl+V 手动粘贴。");Help(habits,owner.Shell.HotkeyAvailable?"唤出面板：Ctrl+Shift+V":"快捷键已被占用，请使用托盘菜单");Tab("general","通用","\uE713",general);

        var account=Page("账号与设备");var memberSummary=Section(account,"当前会员");Row(memberSummary,"会员类型",accountMemberType);Row(memberSummary,"会员状态",accountMemberState);Row(memberSummary,"有效期",accountMemberExpiry);var sync=Section(account,"端到端加密同步");Row(sync,"状态",connection);Row(sync,"待上传",pending);Row(sync,"邮箱",email);Row(sync,"服务",endpoint);Help(sync,"历史同步完成表示本机操作已上传并拉取历史。附件在使用时下载；其他设备是否接收取决于其运行状态。");
        Help(sync,"在此设备保持登录，下次打开接着用");sync.Children.Add(Button("重新验证连接",()=>{owner.ShowLogin(true);return Task.CompletedTask;}));sync.Children.Add(Button("立即同步",async()=>{await owner.SyncNow();await LoadRemote();},true));sync.Children.Add(Button("断开同步连接",async()=>{if(await owner.Dialog(L10n.Text("断开同步连接？"),Palette.Text(L10n.Text("本机加密资料库会保留，连接后才能继续同步。")),L10n.Text("断开连接"))!=ContentDialogResult.Primary)return;await owner.CallCore("disconnect");await owner.RefreshData();owner.ShowLogin();},true));
        var devices=Section(account,"已授权设备");devices.Children.Add(Button("刷新",LoadRemote,true));devices.Children.Add(deviceList);Help(devices,"撤销阻止后续云端访问，无法远程抹除该设备已经解密保存的内容。");var deletion=Section(account,"账户删除");Help(deletion,"删除账户不会取消 Apple 或其他平台的订阅。请前往购买平台管理或取消订阅。");deletion.Children.Add(Button("删除 Clipper 账户",owner.DeleteAccount,true));if(Config["accountDeletionRequest"] is JsonObject receipt){Help(deletion,"删除请求已登记");deletion.Children.Add(Palette.Text(receipt["requestId"]?.ToString()??"",12));}var events=Section(account,"登录记录");events.Children.Add(eventList);Tab("account","账号与设备","\uE77B",account);

        var auth=Page("授权与恢复");var authorize=Section(auth,"授权新设备","扫描新设备的二维码，或输入 12 位授权码。");authorize.Children.Add(Button("扫描授权二维码",owner.ScanAuthorization,true));var code=new TextBox{Header=L10n.Text("12 位授权码"),PlaceholderText="ABCD-2345-WXYZ",MaxLength=20};AutomationProperties.SetAutomationId(code,"settings-auth-code");accountControls.Add(code);authorize.Children.Add(code);authorize.Children.Add(Button("检查请求",()=>owner.AuthorizeCode(code.Text),true));authorize.Children.Add(Button("等待设备授权",owner.ShowAuthorization));var recovery=Section(auth,"恢复文件");Help(recovery,"恢复文件包含解密密钥，请离线保存，不要发送给他人。");recovery.Children.Add(Button("导入恢复文件…",owner.ImportRecoveryFile));recovery.Children.Add(Button("导出恢复文件…",owner.ExportRecoveryFile,true));Tab("authorization","授权与恢复","\uE72E",auth);

        var member=Page("会员");var pro=Section(member,"Clipper");pro.Children.Add(new Image{Height=150,Source=new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","MembershipArtwork.png")))});
        Help(pro,"新账户即享 3 天 Pro，无需付款，到期自动回到 Free。");
        var annual=new ToggleSwitch{Header=L10n.Text("年付"),OnContent=L10n.Text("年付"),OffContent=L10n.Text("月付"),IsOn=true};pro.Children.Add(annual);
        var comparison=new Grid{ColumnSpacing=0,RowSpacing=1};for(int c=0;c<4;c++)comparison.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});pro.Children.Add(comparison);
        void UpdateTotal(){
            comparison.Children.Clear();comparison.RowDefinitions.Clear();string[][] rows=[["套餐对比","Free","Plus","Pro"],["价格","$0",PlanPrice(annual.IsOn?"plus_yearly":"plus_monthly"),PlanPrice(annual.IsOn?"yearly5":"monthly5")],["同步设备","—",availablePlans.Contains("plus_monthly")?"3":"—",availablePlans.Contains("monthly5")?"5":"—"],["云空间","—","以账户权益为准","以账户权益为准"],["历史保留","—","以账户权益为准","以账户权益为准"],["收藏","—","以账户权益为准","以账户权益为准"],["分组","—","以账户权益为准","以账户权益为准"],["单个附件","—","以账户权益为准","以账户权益为准"],["Agent","—","—","规划中 · 尚未实现"]];for(int r=0;r<rows.Length;r++){comparison.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});for(int c=0;c<4;c++){var cell=Palette.Text(L10n.Text(rows[r][c]),14,r==0);var panel=new Border{Child=cell,Padding=new Thickness(12),Background=c==2?new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255,238,246,255)):Palette.White};Grid.SetRow(panel,r);Grid.SetColumn(panel,c);comparison.Children.Add(panel);}}
        }
        void UpdatePurchaseButtons(){foreach(var entry in purchaseButtons)entry.button.IsEnabled=availablePlans.Contains(entry.plan());}
        redrawMembershipPrices=()=>{UpdateTotal();UpdatePurchaseButtons();};annual.Toggled+=(_,_)=>{UpdateTotal();UpdatePurchaseButtons();};UpdateTotal();
        var plus=Button("订阅 Plus",()=>Open("https://cliper.dev/checkout?plan="+(annual.IsOn?"plus_yearly":"plus_monthly")));purchaseButtons.Add((plus,()=>annual.IsOn?"plus_yearly":"plus_monthly"));pro.Children.Add(plus);
        var paid=Button("订阅 Pro",()=>Open("https://cliper.dev/checkout?plan="+(annual.IsOn?"yearly5":"monthly5")));purchaseButtons.Add((paid,()=>annual.IsOn?"yearly5":"monthly5"));pro.Children.Add(paid);UpdatePurchaseButtons();Help(pro,"套餐供应和价格以当前线上目录为准；权益以服务端账户信息为准。");
        Help(pro,"自动续费，可随时取消。");Help(pro,"¹ 受账户共享云空间限制。Agent 尚未实现。月付和年付不另附试用期。");
        var current=Section(member,"当前会员");Row(current,"状态",membershipStatus);Row(current,"有效期",membershipExpiry);Row(current,"来源",membershipSource);Row(current,"授权设备",membershipDevices);Row(current,"云空间",membershipStorage);current.Children.Add(Button("刷新会员",()=>LoadMembership(true),true));current.Children.Add(Button("前往网页查看月付和年付",()=>Open("https://cliper.dev/account/billing")));current.Children.Add(Button("管理订阅",()=>Open((membershipOrigin=="apple"||membershipOrigin=="app_store")?"https://apps.apple.com/account/subscriptions":"https://cliper.dev/account/billing")));var legal=Section(member,"条款与隐私");legal.Children.Add(Button("隐私政策",()=>Open("https://cliper.dev/privacy")));legal.Children.Add(Button("服务条款",()=>Open("https://cliper.dev/terms")));Tab("membership","会员","\uE734",member);

        var privacy=Page("隐私与存储");var storage=Section(privacy,"隐私与附件");Help(storage,"图片自动缓存");Help(storage,"浏览时自动加载图片并加密保存在本机；已缓存内容可以离线查看。");Help(storage,"附件使用时下载");Help(storage,"本机历史先同步；附件在打开、复制图片或导出时下载。");storage.Children.Add(Button("清理本机无引用附件…",async()=>{if(await owner.Dialog(L10n.Text("清理本机无引用附件？"),Palette.Text(L10n.Text("保留历史及待上传内容，不会移除已导出到其他位置的副本。")),L10n.Text("清理"))!=ContentDialogResult.Primary)return;var result=await owner.CallCore("clearUnused") as JsonObject;cleanupResult.Text=L10n.Template("已清理{0}个无引用附件",result?["removed"]?.ToString()??"0");await owner.RefreshData();},true));storage.Children.Add(cleanupResult);Help(storage,"只清理本机已不被历史和待上传队列引用的附件，保留历史。导出到其他位置的副本由你管理。");Tab("privacy","隐私与存储","\uE72E",privacy);

        var about=Page("关于");var brand=new StackPanel{Spacing=18,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,30,0,30)};brand.Children.Add(new Image{Width=84,Height=84,Source=new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","CliperBrand.png")))});brand.Children.Add(Palette.Text("Clipper",32,true));brand.Children.Add(Palette.Text(L10n.Text("原生 Windows 客户端"),15));brand.Children.Add(Palette.Text(typeof(App).Assembly.GetName().Version?.ToString()??""));Help(brand,"本机加密保存，在你的设备间接续。");Help(brand,"关闭设置或面板不会退出应用，Clipper 会在后台常驻。");brand.Children.Add(Button("官方网站",()=>Open("https://cliper.dev")));brand.Children.Add(Button("退出 Clipper",()=>{owner.Shell.Quit();return Task.CompletedTask;},id:"settings-quit"));about.Children.Add(brand);Tab("about","关于","\uE946",about);
    }
    async Task LoadRemote(){
        if(!owner.AccountReady)return;
        var devices=await owner.CallCore("devices");var events=await owner.CallCore("events");if(closed)return;
        deviceList.Children.Clear();var list=devices as JsonArray??(devices as JsonObject)?["devices"] as JsonArray??new();
        if(list.Count==0)Help(deviceList,"没有可显示的设备");
        foreach(var device in list.OfType<JsonObject>()){
            var id=device["deviceId"]?.ToString()??device["id"]?.ToString()??"";bool current=SettingsValues.Boolean(device,"current"),revoked=SettingsValues.Boolean(device,"revoked");
            var row=new Grid{ColumnSpacing=14};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            row.Children.Add(owner.DeviceCard(device));
            var revoke=Button("撤销…",async()=>{var stored=DeviceSecret.Read();var form=new StackPanel{Spacing=14};Help(form,"确认撤销此设备？撤销将阻止后续访问，已解密内容无法远程抹除。");var secret=new PasswordBox{Header=L10n.Text("原资料库口令")};if(stored==null)form.Children.Add(secret);if(await owner.Dialog(L10n.Text("撤销设备授权"),form,L10n.Text("撤销"))!=ContentDialogResult.Primary)return;var pass=stored??secret.Password;if(pass.Length==0)throw new IOException(L10n.Text("请输入原资料库口令"));await owner.CallCore("revoke",new(){["deviceId"]=id,["passphrase"]=pass});secret.Password="";await owner.RefreshData();await LoadRemote();});revoke.IsEnabled=!current&&!revoked;Grid.SetColumn(revoke,1);row.Children.Add(revoke);deviceList.Children.Add(row);
        }
        eventList.Children.Clear();var history=events as JsonArray??(events as JsonObject)?["events"] as JsonArray??new();if(history.Count==0)Help(eventList,"没有可显示的登录记录");
        foreach(var e in history.OfType<JsonObject>().TakeLast(8)){var kind=e["message"]?.ToString()??e["kind"]?.ToString()??e["type"]?.ToString()??"";var stamp=e["created"]?.ToString()??e["timestamp"]?.ToString()??"";eventList.Children.Add(Palette.Text(kind+" · "+FormatDate(stamp),12));}
    }
    string PlanPrice(string plan)=>membershipPrices.TryGetValue(plan,out var amount)&&availablePlans.Contains(plan)?"$"+amount.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+" / "+L10n.Text(plan.Contains("yearly")?"年":"月"):L10n.Text(catalogueUnavailable?"请在网页确认价格":catalogueLoaded?"暂不可用":"价格加载中");
    async Task LoadMembership(bool force){
        accountMemberType.Text="—";accountMemberExpiry.Text="—";accountMemberState.Text=L10n.Text("正在加载会员信息…");
        try { using var http=new System.Net.Http.HttpClient{Timeout=TimeSpan.FromSeconds(10)};http.DefaultRequestHeaders.UserAgent.ParseAdd("Clipper-Windows/0.3");var catalogue=JsonNode.Parse(await http.GetStringAsync("https://cliper.dev/api/catalogue"));if(closed)return;availablePlans.Clear();foreach(var configured in catalogue?["configuredPlans"]?.AsArray()??new JsonArray())if(configured?.ToString() is string configuredId)availablePlans.Add(configuredId);foreach(var item in catalogue?["plans"]?.AsArray()??new JsonArray()){var id=item?["id"]?.ToString();if(id!=null&&new[]{"plus_monthly","plus_yearly","monthly5","yearly5"}.Contains(id)&&item?["currency"]?.ToString()=="usd"&&item?["interval"]?.ToString() is "month" or "year")membershipPrices[id]=item!["amount"]!.GetValue<decimal>()/100;}catalogueLoaded=true;catalogueUnavailable=false;redrawMembershipPrices?.Invoke(); } catch {catalogueUnavailable=true;redrawMembershipPrices?.Invoke();}

        var data=await owner.CallCore(force?"refreshMembership":"membership") as JsonObject;if(closed)return;var access=data?["entitlement"] as JsonObject;
        if(access==null){accountMemberState.Text=L10n.Text("会员信息暂不可用");return;}
        membershipOrigin=access["source"]?.ToString()??"";var plan=access["plan"]?.ToString()??"";
        var limits=access["limits"] as JsonObject??new();membershipDevices.Text=SettingsValues.Boolean(access,"unavailable")?"—":access["role"]?.ToString()=="super_admin"||SettingsValues.Boolean(limits,"unlimitedDevices")?L10n.Text("不限制"):(access["devicesUsed"]?.ToString()??"0")+" / "+(limits["deviceLimit"]?.ToString()??"—");membershipStorage.Text=SettingsValues.Boolean(access,"unavailable")?"—":access["role"]?.ToString()=="super_admin"||SettingsValues.Boolean(limits,"unlimitedStorage")?L10n.Text("不限制"):ClipPresentation.Size(new(){["size"]=limits["storageBytes"]?.DeepClone()});
        bool administrator=access["role"]?.ToString()=="super_admin";bool active=SettingsValues.Boolean(access,"active"),trial=active&&plan=="trial";
        accountMemberType.Text=L10n.Text(administrator?"超级管理员 · Pro":trial?"Pro 试用":!active?"Free 免费版":access["tier"]?.ToString()=="plus"?"Clipper Plus":"Clipper Pro")+(active&&!trial&&(plan.Contains("monthly")||plan.Contains("yearly"))?" · "+L10n.Text(plan.Contains("yearly")?"年付":"月付"):"");
        accountMemberState.Text=L10n.Text(SettingsValues.Boolean(access,"unavailable")?"会员信息暂不可用":trial?"试用中":!active?"永久免费":SettingsValues.Boolean(access,"cancelAtEnd")?"已取消续费，到期后结束":"已开通");
        accountMemberExpiry.Text=active&&access["until"]!=null?L10n.Text(trial?"试用截止时间":"当前周期截止时间")+" · "+FormatDate(access["until"]!.ToString()):L10n.Text(active?"长期有效":"永久免费");
        if(SettingsValues.Boolean(access,"unavailable")){accountMemberType.Text="—";accountMemberExpiry.Text="—";}

        membershipStatus.Text=L10n.Text(SettingsValues.Boolean(access,"unavailable")?"会员信息暂不可用":administrator?"超级管理员 · Pro":!SettingsValues.Boolean(access,"active")?"Free 免费版":plan=="trial"?"免费试用":plan=="preview"?"预览权益":access["tier"]?.ToString()=="plus"?"Clipper Plus":"Clipper Pro");if(SettingsValues.Boolean(access,"trialExpired"))membershipStatus.Text=L10n.Text("Pro 体验已结束，现为 Free。选择月付或年付，升级 Plus 或 Pro。");membershipExpiry.Text=FormatDate(access["until"]?.ToString()??"");membershipSource.Text=administrator?L10n.Text("管理员"):(membershipOrigin=="apple"||membershipOrigin=="app_store")?"App Store":membershipOrigin.Length==0?"—":L10n.Text("网页账户");
    }
    static string FormatDate(string value)=>DevicePresentation.Time(System.Text.Json.Nodes.JsonValue.Create(value));
    static async Task Open(string url){await Windows.System.Launcher.LaunchUriAsync(new Uri(url));}
}
