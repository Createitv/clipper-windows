using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Storage.Streams;
using Windows.Data.Pdf;
using Windows.UI.Text;

namespace ClipWeave;

/// In-app readers; no Office process, shell association, online converter or script execution.
sealed class DocumentPreview : Grid, IDisposable {
    InMemoryRandomAccessStream? input;
    Windows.Media.Playback.MediaPlayer? player;
    bool disposed;
    bool rendering;
    public DocumentPreview(){Background=Palette.White;}
    public void Dispose(){disposed=true;player?.Dispose();player=null;input?.Dispose();input=null;Children.Clear();}
    static Button Command(string title,Action action){var button=new Button{Content=L10n.Text(title)};button.Click+=(_,_)=>action();return button;}
    static TextBox TextBox(string text,bool code=false){
        var box=new TextBox{Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=0,BorderThickness=new Thickness(0),Background=Palette.White,Padding=new Thickness(20),HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
        if(code)box.FontFamily=new FontFamily("Consolas");
        ScrollViewer.SetVerticalScrollBarVisibility(box,ScrollBarVisibility.Auto);ScrollViewer.SetHorizontalScrollBarVisibility(box,ScrollBarVisibility.Auto);return box;
    }
    public void ShowText(string text,bool code=false,bool extracted=false){
        Children.Clear();RowDefinitions.Clear();RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var pages=new PreviewText(text);var current=0;var found=-1;
        var bar=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,Margin=new Thickness(16,8,16,8)};
        var body=TextBox(pages.Page(0),code);Grid.SetRow(body,1);Children.Add(body);
        var label=new TextBlock{VerticalAlignment=VerticalAlignment.Center};
        var jump=new NumberBox{Minimum=1,Maximum=pages.Count,Value=1,Width=95,SmallChange=1,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        void Navigate(int index){current=Math.Clamp(index,0,pages.Count-1);body.Text=pages.Page(current);jump.Value=current+1;label.Text=$"/ {pages.Count}"+(extracted?" · "+L10n.Text("文本预览（不保留原版式）"):"");}
        bar.Children.Add(Command("上一页",()=>Navigate(current-1)));bar.Children.Add(jump);bar.Children.Add(label);bar.Children.Add(Command("下一页",()=>Navigate(current+1)));
        var search=new TextBox{PlaceholderText=L10n.Text("搜索全文"),Width=160};bar.Children.Add(search);
        void Find(){if(search.Text.Length==0)return;found=pages.Find(search.Text,found+1);if(found<0)found=pages.Find(search.Text,0);if(found>=0){Navigate(pages.PageAt(found));body.Focus(FocusState.Programmatic);body.Select(found-pages.Start(current),Math.Min(search.Text.Length,body.Text.Length-(found-pages.Start(current))));}else label.Text=L10n.Text("没有找到匹配记录");}
        search.TextChanged+=(_,_)=>found=-1;search.KeyDown+=(_,e)=>{if(e.Key==Windows.System.VirtualKey.Enter){Find();e.Handled=true;}};
        bar.Children.Add(Command("查找下一个",Find));
        jump.ValueChanged+=(_,e)=>{if(double.IsFinite(e.NewValue))Navigate((int)e.NewValue-1);};
        Children.Add(new ScrollViewer{Content=bar,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});Navigate(0);
    }
    async Task<InMemoryRandomAccessStream> Stream(byte[] bytes){var stream=new InMemoryRandomAccessStream();using(var writer=new DataWriter(stream)){writer.WriteBytes(bytes);await writer.StoreAsync();writer.DetachStream();}stream.Seek(0);return stream;}
    public async Task Show(JsonObject content,string name){
        if(PreviewText.HasOriginalText(content)){ShowText(PreviewText.Content(content));return;}
        if(content["document"]?["text"] is JsonValue value){ShowText(value.GetValue<string>(),true,content["document"]?["kind"]?.ToString()=="document");return;}
        byte[] bytes=content["bytes"] is JsonValue raw?Convert.FromBase64String(raw.GetValue<string>()):Array.Empty<byte>();
        var kind=content["document"]?["kind"]?.ToString()??"unsupported";
        if(kind=="image"||content["type"]?.ToString()=="image"){
            using var stream=await Stream(bytes);var bitmap=new BitmapImage();await bitmap.SetSourceAsync(stream);if(!disposed){var image=new Image{Source=bitmap,Stretch=Stretch.Uniform};Children.Add(new ScrollViewer{Content=image,ZoomMode=ZoomMode.Enabled,MinZoomFactor=0.1f,MaxZoomFactor=8,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});}return;
        }
        if(kind=="pdf"){
            input=await Stream(bytes);if(disposed)return;
            async Task Load(string password){
                try{input.Seek(0);var doc=await PdfDocument.LoadFromStreamAsync(input,password);if(disposed)return;ShowPDF(doc);}
                catch{if(disposed)return;Children.Clear();var panel=new StackPanel{Spacing=12,Margin=new Thickness(24)};panel.Children.Add(new TextBlock{Text=L10n.Text("PDF 无法打开；如已加密，请输入密码重试。"),TextWrapping=TextWrapping.Wrap});var secret=new PasswordBox{PlaceholderText=L10n.Text("密码")};panel.Children.Add(secret);var retry=new Button{Content=L10n.Text("重试")};retry.Click+=async(_,_)=>{retry.IsEnabled=false;await Load(secret.Password);secret.Password="";retry.IsEnabled=true;};panel.Children.Add(retry);Children.Add(panel);}
            }
            await Load("");return;
        }
        if(kind=="rtf"){
            var box=new RichEditBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20)};
            ScrollViewer.SetVerticalScrollBarVisibility(box,ScrollBarVisibility.Auto);
            try{box.Document.SetText(TextSetOptions.FormatRtf,Encoding.Latin1.GetString(bytes));Children.Add(box);}catch{ShowText(L10n.Text("此文件暂不支持完整预览。"));}return;
        }
        if(kind=="media"){
            input=await Stream(bytes);if(disposed)return;
            var ext=Path.GetExtension(name).ToLowerInvariant();var mime=ext switch{".mp3"=>"audio/mpeg",".wav"=>"audio/wav",".m4a" or ".aac"=>"audio/mp4",".flac"=>"audio/flac",".ogg"=>"audio/ogg",".webm"=>"video/webm",".mov"=>"video/quicktime",_=>"video/mp4"};
            player=new(){AutoPlay=false,Source=Windows.Media.Core.MediaSource.CreateFromStream(input,mime)};
            var media=new MediaPlayerElement{AreTransportControlsEnabled=true};media.SetMediaPlayer(player);Children.Add(media);return;
        }
        // Unknown/binary formats remain inspectable without launching a third-party app.
        var hex=Convert.ToHexString(bytes.AsSpan(0,Math.Min(4096,bytes.Length)));
        var lines=Enumerable.Range(0,(hex.Length+63)/64).Select(i=>hex.Substring(i*64,Math.Min(64,hex.Length-i*64)));
        ShowText(name+"\n"+L10n.Text("此文件暂不支持完整预览。")+"\n"+(content["documentError"]?.ToString()??"")+"\n"+bytes.Length+" bytes\n\n"+string.Join("\n",lines),true);
    }
    void ShowPDF(PdfDocument doc){
        Children.Clear();RowDefinitions.Clear();RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var index=0;var image=new Image{Stretch=Stretch.Uniform};var scroll=new ScrollViewer{Content=image,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);Children.Add(scroll);
        var bar=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,Margin=new Thickness(16)};
        var jump=new NumberBox{Value=1,Minimum=1,Maximum=doc.PageCount,Width=95,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        var status=new TextBlock{VerticalAlignment=VerticalAlignment.Center};
        async Task Render(int requested){if(rendering||disposed||doc.PageCount==0)return;rendering=true;try{index=Math.Clamp(requested,0,(int)doc.PageCount-1);using var page=doc.GetPage((uint)index);using var stream=new InMemoryRandomAccessStream();await page.RenderToStreamAsync(stream,new PdfPageRenderOptions{DestinationWidth=1600});stream.Seek(0);var bitmap=new BitmapImage();await bitmap.SetSourceAsync(stream);if(!disposed){image.Source=bitmap;status.Text=$"{index+1} / {doc.PageCount}";jump.Value=index+1;}}catch{status.Text=L10n.Text("预览不可用");}finally{rendering=false;}}
        bar.Children.Add(Command("上一页",()=>_=Render(index-1)));bar.Children.Add(jump);bar.Children.Add(status);bar.Children.Add(Command("下一页",()=>_=Render(index+1)));jump.ValueChanged+=(_,e)=>{if(double.IsFinite(e.NewValue))_=Render((int)e.NewValue-1);};Children.Add(bar);_=Render(0);
    }
}
