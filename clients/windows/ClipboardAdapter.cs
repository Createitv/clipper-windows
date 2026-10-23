using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace ClipWeave;
public sealed partial class MainWindow {
    uint lastClipboardSequence,ownClipboardSequence;
    async Task CaptureClipboard() {
        var config=status["config"] as JsonObject??new();if(config["captureEnabled"]?.GetValue<bool>()!=true)return;
        var sequence=GetClipboardSequenceNumber();if(sequence==lastClipboardSequence||sequence==ownClipboardSequence)return;lastClipboardSequence=sequence;
        var owner=GetClipboardOwner();GetWindowThreadProcessId(owner,out var pid);if(pid==(uint)Environment.ProcessId)return;
        string source="";try{source=Process.GetProcessById((int)pid).ProcessName+".exe";}catch{}
        if(Str(config,"excludedApps").Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(source,StringComparer.OrdinalIgnoreCase))return;
        if(HistoryExcluded())return;
        DataPackageView content;
        try{content=Clipboard.GetContent();if(content.AvailableFormats.Any(ClipPresentation.IsSensitiveFormat))return;}catch(COMException){return;}
        if(config["captureFiles"]?.GetValue<bool>()==true&&content.Contains(StandardDataFormats.StorageItems)) {
            var items=await content.GetStorageItemsAsync();
            foreach(var item in items.Take(20)) {
                if(string.IsNullOrWhiteSpace(item.Path))continue;
                await core.Call(item is StorageFolder?"importFolder":"importFile",new(){["path"]=item.Path});
            }
            return;
        }
        if(content.Contains(StandardDataFormats.Bitmap)) {
            using var input=await (await content.GetBitmapAsync()).OpenReadAsync();if(input.Size>16*1024*1024)return;
            var decoder=await BitmapDecoder.CreateAsync(input);if((long)decoder.PixelWidth*decoder.PixelHeight>16_000_000)return;
            using var bitmap=await decoder.GetSoftwareBitmapAsync();using var png=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,png);encoder.SetSoftwareBitmap(bitmap);await encoder.FlushAsync();if(png.Size>20*1024*1024)return;
            png.Seek(0);using var reader=new DataReader(png);await reader.LoadAsync((uint)png.Size);var bytes=new byte[(int)png.Size];reader.ReadBytes(bytes);
            await core.Call("importBytes",new(){["name"]="Clipboard.png",["base64"]=Convert.ToBase64String(bytes)});return;
        }
        if(content.Contains(StandardDataFormats.Text)) {
            var text=await content.GetTextAsync();var html=content.Contains(StandardDataFormats.Html)?HtmlFormatHelper.GetStaticFragment(await content.GetHtmlFormatAsync()):"";
            if(string.IsNullOrEmpty(text)||text==lastCaptured||System.Text.Encoding.UTF8.GetByteCount(text)+System.Text.Encoding.UTF8.GetByteCount(html)>32*1024)return;
            await core.Call("addText",new(){["text"]=text,["html"]=html});
        }
    }
    static bool HistoryExcluded() {
        var ignore=RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");if(IsClipboardFormatAvailable(ignore))return true;
        var format=RegisterClipboardFormat("CanIncludeInClipboardHistory");if(!IsClipboardFormatAvailable(format)||!OpenClipboard(IntPtr.Zero))return false;
        try{var data=GetClipboardData(format);if(data==IntPtr.Zero||GlobalSize(data).ToUInt64()<4)return false;var pointer=GlobalLock(data);if(pointer==IntPtr.Zero)return false;try{return Marshal.ReadInt32(pointer)==0;}finally{GlobalUnlock(data);}}
        finally{CloseClipboard();}
    }
    [DllImport("user32.dll")]static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")]static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")]static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")]static extern bool CloseClipboard();
    [DllImport("user32.dll")]static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")]static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]static extern UIntPtr GlobalSize(IntPtr memory);
}
