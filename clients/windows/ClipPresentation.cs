using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace ClipWeave;
public readonly record struct PanelRectangle(int X,int Y,int Width,int Height);
public static class ClipPresentation {
    public static string Text(JsonObject clip,string key,string fallback="")=>clip[key]?.ToString()??fallback;
    public static Uri? Link(JsonObject clip) {
        var text=Text(clip,"text",Text(clip,"title"));
        var match=Regex.Match(text,@"https?://[^\s<>""']+",RegexOptions.IgnoreCase);
        return match.Success&&Uri.TryCreate(match.Value.TrimEnd('.',',',';',')',']','。','，'),UriKind.Absolute,out var uri)&&uri.Host.Length>0?uri:null;
    }
    public static string Kind(JsonObject clip)=>Link(clip)!=null?"link":Text(clip,"type","text");
    public static bool Matches(JsonObject clip,string query,string group,string kind) {
        if(group=="★"&&clip["favourite"]?.GetValue<bool>()!=true)return false;
        if(group!=""&&group!="★"&&Text(clip,"group")!=group)return false;
        if(kind!=""&&Kind(clip)!=kind)return false;
        return string.IsNullOrWhiteSpace(query)||string.Join(" ",new[]{Text(clip,"title"),Text(clip,"text"),Text(clip,"name"),Text(clip,"group")}).Contains(query,StringComparison.OrdinalIgnoreCase);
    }
    public static PanelRectangle PanelBounds(int left,int top,int width,int height,double scale,double requestedHeight=430) {
        if(!double.IsFinite(scale)||scale<=0)scale=1;
        if(!double.IsFinite(requestedHeight))requestedHeight=430;
        requestedHeight=Math.Clamp(requestedHeight,400,760);
        var gap=Math.Min((int)Math.Round(12*scale),Math.Max(0,Math.Min(width,height)/4));
        return new(left+gap,top+height-gap-Math.Min((int)Math.Round(requestedHeight*scale),Math.Max(1,height-2*gap)),Math.Max(1,width-2*gap),Math.Min((int)Math.Round(requestedHeight*scale),Math.Max(1,height-2*gap)));
    }
    public static bool IsSensitiveFormat(string value)=>new[]{"ExcludeClipboardContentFromMonitorProcessing","Clipboard Viewer Ignore","com.agilebits.onepassword","org.nspasteboard.ConcealedType","org.clipweave.own-write"}.Contains(value,StringComparer.OrdinalIgnoreCase);
    public static string Size(JsonObject clip) {
        if(!long.TryParse(Text(clip,"size"),out var size)||size<=0)return L10n.Text("端到端加密");
        return size>=1024*1024?$"{size/(1024.0*1024):0.#} MB":size>=1024?$"{size/1024.0:0.#} KB":$"{size} B";
    }
    public static string Time(JsonObject clip) {
        if(!long.TryParse(Text(clip,"created"),out var stamp))return "";
        try{return DateTimeOffset.FromUnixTimeMilliseconds(stamp).ToLocalTime().ToString("MM/dd HH:mm");}catch{return "";}
    }
    public static string KindLabel(string kind)=>L10n.Text(kind switch{"text"=>"文字","link"=>"链接","image"=>"图片","file"=>"文件","folder"=>"文件夹","code"=>"代码","svg"=>"矢量图",_=>"内容"});
}
