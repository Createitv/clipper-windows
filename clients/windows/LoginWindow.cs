using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json.Nodes;
using WinUIEx;
namespace ClipWeave;
sealed class LoginWindow:Window {
    readonly MainWindow owner;
    readonly TextBox email=new(){Header=L10n.Text("邮箱"),PlaceholderText="you@example.com"};
    readonly TextBox code=new(){Header=L10n.Text("邮箱验证码"),MaxLength=6};
    readonly PasswordBox password=new(){Header=L10n.Text("账户密码")};
    readonly PasswordBox confirm=new(){Header=L10n.Text("确认密码")};
    readonly TextBlock notice=Palette.Text("");
    readonly Button submit=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly Button send=new(){Content=L10n.Text("发送验证码")};
    readonly StackPanel form=new(){Spacing=18,MaxWidth=350,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(42)};
    readonly ContentControl formHost=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch};
    internal bool IsBusy=>busy;
    bool passwordMode=true,resetting,busy;string ticket="";
    public LoginWindow(MainWindow owner){
        this.owner=owner;Title="Clipper";
        var root=new Grid{Background=Palette.White};root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        var art=new Grid{Background=Palette.Soft};art.Children.Add(new Image{Source=new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","LoginArtwork.png"))),Stretch=Stretch.UniformToFill});root.Children.Add(art);
        var brand=new StackPanel{Spacing=10,Margin=new Thickness(30),VerticalAlignment=VerticalAlignment.Top};brand.Children.Add(Palette.Text("Clipper",28,true));brand.Children.Add(Palette.Text(L10n.Text("你的剪贴板，随处可用。"),16));art.Children.Add(brand);
        form.Children.Add(Palette.Text(L10n.Text("欢迎使用 Clipper"),27,true));form.Children.Add(Palette.Text(L10n.Text("登录并同步云端历史，本机仅保留缓存。")));
        var tabs=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};var passTab=new Button{Content=L10n.Text("密码登录")};var codeTab=new Button{Content=L10n.Text("验证码登录")};passTab.Click+=(_,_)=>{passwordMode=true;resetting=false;ticket="";Update();};codeTab.Click+=(_,_)=>{passwordMode=false;resetting=false;ticket="";Update();};tabs.Children.Add(passTab);tabs.Children.Add(codeTab);form.Children.Add(tabs);
        email.Text=(owner.Status["config"] as JsonObject)?["email"]?.ToString()??"";
        foreach(var child in new UIElement[]{email,password,confirm,code,send})form.Children.Add(child);
        var reset=new HyperlinkButton{Content=L10n.Text("忘记密码 / 首次设置密码"),Padding=new Thickness(0)};reset.Click+=(_,_)=>{passwordMode=true;resetting=true;ticket="";Update();};form.Children.Add(reset);
        submit.Style=(Style)Application.Current.Resources["AccentButtonStyle"];submit.Click+=async(_,_)=>await Perform(async()=>{
            if(string.IsNullOrWhiteSpace(email.Text))throw new IOException(L10n.Text("请填写邮箱"));
            if((resetting||ticket.Length>0)&&(password.Password.Length<8||password.Password.Length>128||password.Password!=confirm.Password))throw new IOException(L10n.Text("请输入两次一致的 8–128 字符密码。"));
            var result=await owner.SignIn(email.Text.Trim(),password.Password,ticket.Length>0?ticket:((resetting||!passwordMode)?code.Text:""),passwordMode||ticket.Length>0);
            if(result["syncNotice"] is JsonNode syncNotice)notice.Text=L10n.Text(syncNotice.ToString());
            if(result["passwordSetupRequired"]?.GetValue<bool>()==true){ticket=result["verificationToken"]?.ToString()??"";passwordMode=true;resetting=true;notice.Text=L10n.Text("邮箱已验证，请设置密码后继续。");Update();}
        });form.Children.Add(submit);if(owner.Status["pendingPairing"] is JsonObject){var pending=new Button{Content=L10n.Text("等待设备授权"),HorizontalAlignment=HorizontalAlignment.Stretch};pending.Click+=async(_,_)=>await Perform(async()=>{await owner.ShowAuthorization();await owner.RefreshData();if(owner.AccountReady){Close();owner.Shell.ShowPanel();}});form.Children.Add(pending);}if(owner.CanUnlock){var unlock=new Button{Content=L10n.Text("使用设备安全凭据解锁"),HorizontalAlignment=HorizontalAlignment.Stretch};unlock.Click+=async(_,_)=>await Perform(owner.UnlockDevice);form.Children.Add(unlock);}form.Children.Add(notice);
        send.Click+=async(_,_)=>await Perform(async()=>{await owner.CallCore("requestCode",new(){["endpoint"]="https://api.cliper.dev",["email"]=email.Text.Trim()});notice.Text=L10n.Text("已请求验证码，请查看邮箱。");});
        formHost.Content=form;var scroll=new ScrollViewer{Content=formHost,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(scroll,1);root.Children.Add(scroll);Content=root;AppWindow.Resize(new Windows.Graphics.SizeInt32(960,700));this.CenterOnScreen();Closed+=(_,_)=>owner.LoginClosed();Update();
    }
    void Update(){password.Visibility=passwordMode?Visibility.Visible:Visibility.Collapsed;confirm.Visibility=resetting?Visibility.Visible:Visibility.Collapsed;code.Visibility=send.Visibility=(!passwordMode||resetting)&&ticket.Length==0?Visibility.Visible:Visibility.Collapsed;submit.Content=L10n.Text(ticket.Length>0?"保存密码并登录":"登录");}
    async Task Perform(Func<Task> operation){if(busy)return;busy=true;formHost.IsEnabled=false;notice.Text="";try{await operation();}catch(Exception e){notice.Text=L10n.Text(e.Message);}finally{busy=false;formHost.IsEnabled=true;}}
}
