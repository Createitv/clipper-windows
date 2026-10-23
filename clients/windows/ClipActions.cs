using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using WinUIEx;

namespace ClipWeave;
public sealed partial class MainWindow {
    async Task AddText(){
        var input=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=220,MinWidth=420,PlaceholderText=L10n.Text("添加文字")};var form=new StackPanel{Spacing=16};form.Children.Add(input);var group=ImportGroups();form.Children.Add(group);
        while(await Dialog(L10n.Text("添加文字"),form,L10n.Text("保存"))!=ContentDialogResult.Primary){if(input.Text.Length==0||await Dialog(L10n.Text("放弃尚未保存的文字？"),Palette.Text(L10n.Text("未保存内容不会加入资料库。")),L10n.Text("放弃草稿"))==ContentDialogResult.Primary)return;}
        if(string.IsNullOrWhiteSpace(input.Text))return;if(System.Text.Encoding.UTF8.GetByteCount(input.Text)>32768)throw new IOException(L10n.Text("文本不能为空且不能超过 32 KiB"));
        var created=await core.Call("addText",new(){["text"]=input.Text}) as JsonObject??new();var target=ChosenGroup(group);
        while(target.Length>0){try{await core.Call("group",new(){["clipId"]=Str(created,"id"),["name"]=target});break;}catch(Exception failure){if(await Dialog(L10n.Text("操作未完成"),Palette.Text(L10n.Error(failure.Message)),L10n.Text("重试"))!=ContentDialogResult.Primary)break;}}
        await Refresh();
    }
    async Task<string> ExportTemporary(JsonObject clip) {
        var directory=Path.Combine(Path.GetTempPath(),"Clipper","Previews",Guid.NewGuid().ToString());Directory.CreateDirectory(directory);previewDirectories.Add(directory);
        var path=Path.Combine(directory,SafeName(clip));await core.Call("export",new(){["clipId"]=Str(clip,"id"),["path"]=path});return path;
    }
    static string SafeName(JsonObject clip){var name=Path.GetFileName(Str(clip,"name",Str(clip,"title","Clipper.txt")));name=new string(name.Take(140).Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c).ToArray()).Trim();if(name.Length==0)name="Clipper.txt";return name;}
    async Task CopyClip(JsonObject clip) {
        var content=await core.Call("preview",new(){["clipId"]=Str(clip,"id")}) as JsonObject??new();
        var package=new DataPackage{RequestedOperation=DataPackageOperation.Copy};
        if(content["text"]!=null){package.SetText(Str(content,"text"));if(content["html"]!=null)package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Str(content,"html")));}
        else {
            var path=await ExportTemporary(clip);
            if(Directory.Exists(path))package.SetStorageItems(new IStorageItem[]{await StorageFolder.GetFolderFromPathAsync(path)});
            else {
                var file=await StorageFile.GetFileFromPathAsync(path);package.SetStorageItems(new IStorageItem[]{file});
                if(ClipPresentation.Kind(clip)=="image")package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            }
        }
        package.SetData("org.clipweave.own-write",true);Clipboard.SetContent(package);Clipboard.Flush();ownClipboardSequence=GetClipboardSequenceNumber();
        SetNotice(L10n.Text("已复制"));
        if(previewWindow!=null)previewWindow.Close();
        await Shell.PasteToPreviousAsync((status["config"] as JsonObject)?["directPaste"]?.GetValue<bool>()==true);
    }
    async Task PreviewClip(JsonObject clip) {
        var content=await core.Call("preview",new(){["clipId"]=Str(clip,"id")}) as JsonObject??new();
        previewWindow?.Close();var window=new Window{Title=L10n.Text("内容预览")+" — Clipper"};previewWindow=window;
        var root=new Grid{Background=Palette.White};root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var bar=new Grid{Padding=new Thickness(16),ColumnSpacing=14};bar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});bar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        bar.Children.Add(Palette.Text(Str(clip,"title",Str(clip,"name",L10n.Text("内容预览"))),15,true));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};Grid.SetColumn(actions,1);bar.Children.Add(actions);
        var favorite=Palette.Icon(clip["favourite"]?.GetValue<bool>()==true?"\uE735":"\uE734","收藏",()=>{});favorite.Click+=async(_,_)=>await Run(async()=>{await Action("favourite",new(){["clipId"]=Str(clip,"id")});var updated=clips.FirstOrDefault(c=>Str(c,"id")==Str(clip,"id"));favorite.Content=new FontIcon{Glyph=updated?["favourite"]?.GetValue<bool>()==true?"\uE735":"\uE734",FontSize=16};});actions.Children.Add(favorite);
        actions.Children.Add(Palette.Icon("\uE8C8","复制",()=>_=Run(()=>CopyClip(clip))));actions.Children.Add(Palette.Icon("\uE72D","分享",()=>_=Run(()=>ShareClip(clip))));
        var more=Palette.Icon("\uE712","更多",()=>{});var moreMenu=new MenuFlyout();MenuItem(moreMenu,L10n.Text("所属分组")+" · "+Str(clip,"group",L10n.Text("未分组")),()=>_=Run(()=>MoveClip(clip)));MenuItem(moreMenu,"导出…",()=>_=Run(()=>ExportClip(clip)));MenuItem(moreMenu,"删除…",()=>_=Run(async()=>{await DeleteClip(clip);if(!clips.Any(c=>Str(c,"id")==Str(clip,"id")))window.Close();}));more.Flyout=moreMenu;actions.Children.Add(more);
        actions.Children.Add(Palette.Icon("\uE711","关闭",window.Close));root.Children.Add(bar);
        if(ClipPresentation.Link(clip) is Uri uri) {
            var container=new Grid();container.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});container.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            var open=new Button{Content=uri.Host+" · "+L10n.Text("在默认浏览器中打开"),Margin=new Thickness(20,0,20,12)};open.Click+=async(_,_)=>await Windows.System.Launcher.LaunchUriAsync(uri);container.Children.Add(open);
            var browser=new WebView2();Grid.SetRow(browser,1);container.Children.Add(browser);Grid.SetRow(container,1);root.Children.Add(container);
            browser.Loaded+=async(_,_)=>{try{await browser.EnsureCoreWebView2Async();browser.CoreWebView2.Settings.IsWebMessageEnabled=false;browser.CoreWebView2.NewWindowRequested+=async(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var target)&&target.Scheme is "http" or "https")await Windows.System.Launcher.LaunchUriAsync(target);};browser.Source=uri;}catch{container.Children.Remove(browser);var fallback=Palette.Text(L10n.Text("网页预览暂不可用，请使用默认浏览器打开。"));fallback.Margin=new Thickness(20);Grid.SetRow(fallback,1);container.Children.Add(fallback);}};
            window.Closed+=(_,_)=>browser.Close();
        } else if(content["bytes"]!=null&&ClipPresentation.Kind(clip)=="image") {
            var image=new Image{Source=await DecodeImage(Convert.FromBase64String(Str(content,"bytes"))),Stretch=Stretch.Uniform,Margin=new Thickness(20)};var imagePanel=new Grid();imagePanel.Children.Add(image);var expand=Palette.Icon("\uE740","打开完整图片",()=>_=Run(()=>OpenFullImage(clip)));expand.HorizontalAlignment=HorizontalAlignment.Right;expand.VerticalAlignment=VerticalAlignment.Bottom;expand.Margin=new Thickness(24);imagePanel.Children.Add(expand);Grid.SetRow(imagePanel,1);root.Children.Add(imagePanel);
        } else if(content["text"]!=null) {
            var text=new TextBox{Text=Str(content,"text"),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20),BorderThickness=new Thickness(0),Background=Palette.White};if(ClipPresentation.Kind(clip)=="code")text.FontFamily=new FontFamily("Consolas");Grid.SetRow(text,1);root.Children.Add(text);
        } else {
            var summary=new StackPanel{Spacing=18,Margin=new Thickness(40)};summary.Children.Add(Palette.Text(Str(clip,"name",Str(clip,"title")),20,true));summary.Children.Add(Palette.Text(ClipPresentation.Size(clip)));if(ClipPresentation.Kind(clip)=="folder"||new[]{".png",".jpg",".jpeg",".gif",".webp",".bmp",".pdf",".txt",".md",".mp3",".mp4",".wav",".mov"}.Contains(Path.GetExtension(SafeName(clip)).ToLowerInvariant()))summary.Children.Add(Button(L10n.Text("打开附件"),async()=>{var path=await ExportTemporary(clip);if(File.Exists(path))await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));else await Windows.System.Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path));}));summary.Children.Add(Button(L10n.Text("导出…"),()=>ExportClip(clip)));Grid.SetRow(summary,1);root.Children.Add(summary);
        }
        root.KeyDown+=(_,e)=>{if(e.Key==Windows.System.VirtualKey.Escape){window.Close();e.Handled=true;}};window.Content=root;WindowAppearance.Apply(window);window.AppWindow.Resize(new Windows.Graphics.SizeInt32(800,600));window.CenterOnScreen();window.Closed+=(_,_)=>{if(previewWindow==window)previewWindow=null;};window.Activate();Shell.HidePanel();
    }
    async Task ExportClip(JsonObject clip) {
        var picker=new Windows.Storage.Pickers.FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(DialogOwner));var folder=await picker.PickSingleFolderAsync();if(folder==null)return;
        var path=Path.Combine(folder.Path,SafeName(clip));if((File.Exists(path)||Directory.Exists(path))&&await Dialog(L10n.Text("目标已存在"),Palette.Text(L10n.Text("导出将覆盖同名内容，是否继续？")),L10n.Text("覆盖"))!=ContentDialogResult.Primary)return;
        await core.Call("export",new(){["clipId"]=Str(clip,"id"),["path"]=path});SetNotice(L10n.Text("已导出"));
    }
    async Task MoveClip(JsonObject? clip) {
        var input=new TextBox{Header=L10n.Text("分组名称"),Text=clip==null?"":Str(clip,"group"),MinWidth=320};var form=new StackPanel{Spacing=14};
        if(clip!=null){var choices=new ComboBox{Header=L10n.Text("选择分组"),HorizontalAlignment=HorizontalAlignment.Stretch};var groups=(status["groups"] as JsonArray??new()).Select(x=>x?.ToString()??"").ToList();choices.Items.Add(L10n.Text("未分组"));foreach(var name in groups)choices.Items.Add(name);choices.SelectedIndex=groups.IndexOf(input.Text)+1;choices.SelectionChanged+=(_,_)=>{if(choices.SelectedIndex>=0)input.Text=choices.SelectedIndex==0?"":groups[choices.SelectedIndex-1];};form.Children.Add(choices);}
        form.Children.Add(input);if(await Dialog(L10n.Text(clip==null?"新建分组":"移到分组"),form,L10n.Text("保存"))==ContentDialogResult.Primary)await Action("group",new(){["clipId"]=clip==null?"":Str(clip,"id"),["name"]=input.Text});Render();
    }
    async Task DeleteClip(JsonObject clip){if(await Dialog(L10n.Text("删除这条记录？"),Palette.Text(L10n.Text("删除会同步到已连接的设备，系统剪贴板中的当前内容会保留。")),L10n.Text("删除"))==ContentDialogResult.Primary)await Action("delete",new(){["clipId"]=Str(clip,"id")});}
    static async Task<BitmapImage> DecodeImage(byte[] bytes,int width=1200){using var stream=new InMemoryRandomAccessStream();using(var writer=new DataWriter(stream)){writer.WriteBytes(bytes);await writer.StoreAsync();writer.DetachStream();}stream.Seek(0);var image=new BitmapImage{DecodePixelWidth=width};await image.SetSourceAsync(stream);return image;}
}
