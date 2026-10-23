using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace ClipWeave;

public static class AuthorizationScanner {
    public static Image CodeImage(string code) {
        var pixels=AuthorizationCode.Encode(code);
        var bitmap=new WriteableBitmap(pixels.Width,pixels.Height);
        using(var stream=bitmap.PixelBuffer.AsStream())stream.Write(pixels.Pixels);
        bitmap.Invalidate();
        return new Image { Source=bitmap,Width=232,Height=232,HorizontalAlignment=HorizontalAlignment.Center };
    }
    public static async Task<string?> ScanAsync(XamlRoot root) {
        var preview=new Image { Width=360,Height=260,Stretch=Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var message=new TextBlock { Text=L10n.Text("将授权二维码放入取景框"),TextWrapping=TextWrapping.Wrap };
        var panel=new StackPanel { Spacing=12 };panel.Children.Add(preview);panel.Children.Add(message);
        var dialog=new ContentDialog { Title=L10n.Text("扫描授权二维码"),Content=panel,CloseButtonText=L10n.Text("取消"),XamlRoot=root };
        using var stop=new CancellationTokenSource();
        string? result=null;
        Task loop=Task.CompletedTask;
        dialog.Opened+=(_,_)=>{loop=Capture();};
        dialog.Closed+=(_,_)=>stop.Cancel();
        try {await dialog.ShowAsync();} finally {stop.Cancel();await loop;}
        return result;

        async Task Capture() {
            using var camera=new MediaCapture();
            try {
                await camera.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode=StreamingCaptureMode.Video });
                while(!stop.IsCancellationRequested) {
                    using var frame=new InMemoryRandomAccessStream();
                    await camera.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(),frame);
                    if(stop.IsCancellationRequested)return;
                    frame.Seek(0);
                    var decoder=await BitmapDecoder.CreateAsync(frame);
                    var data=await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,new BitmapTransform(),ExifOrientationMode.RespectExifOrientation,ColorManagementMode.DoNotColorManage);
                    using var bitmap=await decoder.GetSoftwareBitmapAsync();
                    var source=new SoftwareBitmapSource();
                    using var display=SoftwareBitmap.Convert(bitmap,BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);
                    await source.SetBitmapAsync(display);
                    if(stop.IsCancellationRequested)return;
                    preview.Source=source;
                    result=AuthorizationCode.Decode(data.DetachPixelData(),(int)decoder.OrientedPixelWidth,(int)decoder.OrientedPixelHeight);
                    if(result!=null){dialog.Hide();return;}
                    await Task.Delay(600,stop.Token);
                }
            } catch(OperationCanceledException) { }
            catch(Exception) {if(!stop.IsCancellationRequested)message.Text=L10n.Text("相机暂不可用，请手动输入授权码。");}
        }
    }
}
