using System.Globalization;
using ClipperL10n = ClipWeave.L10n;
static void Equal(string actual,string expected) { if(actual!=expected) throw new Exception($"Expected {expected}, got {actual}"); }
foreach(var locale in new[]{"en-US","zh-CN","zh-TW","ja-JP"}) {
    CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(locale);
    var chinese=locale.StartsWith("zh");
    Equal(ClipperL10n.Text("邮箱地址"),chinese?"邮箱地址":"Email address");
    Equal(ClipperL10n.Text("vault is locked"),chinese?"资料库已锁定":"vault is locked");
    Equal(ClipperL10n.Text("unknown server error"),"unknown server error");
    Equal(ClipperL10n.Template("{0} / {1}","用户 {1}","文件.txt"),"用户 {1} / 文件.txt");
    Console.WriteLine($"Localization passed: {locale}");
}
var normalized=ClipWeave.AuthorizationCode.Normalize("abcd-2345-wxyz");
Equal(normalized ?? "","ABCD2345WXYZ");
var pixels=ClipWeave.AuthorizationCode.Encode(normalized!);
Equal(ClipWeave.AuthorizationCode.Decode(pixels.Pixels,pixels.Width,pixels.Height) ?? "",normalized!);
foreach(var invalid in new[]{"123456","ABCD2345WX0Z","ABCD2345WXIZ","https://example.com/ABCD2345WXYZ"})
    if(ClipWeave.AuthorizationCode.Normalize(invalid)!=null)throw new Exception("Accepted invalid authorization code");
Console.WriteLine("Authorization QR roundtrip and input validation passed");
var payload="cliper-pair-v1:ABCD2345WXYZ:0123456789ABCDEF";
var qr=ClipWeave.AuthorizationCode.Encode(payload);
Equal(ClipWeave.AuthorizationCode.Decode(qr.Pixels,qr.Width,qr.Height) ?? "",payload);
foreach(var invalid in new[]{"cliper-pair-v1:ABCD2345WXYZ:","cliper-pair-v2:ABCD2345WXYZ:0123456789ABCDEF"})
    if(ClipWeave.AuthorizationCode.Payload(invalid)!=null)throw new Exception("Accepted unbound authorization QR");
var clip=new System.Text.Json.Nodes.JsonObject{["type"]="text",["text"]="Design https://cliper.dev/features",["group"]="Work",["favourite"]=true};
Equal(ClipWeave.ClipPresentation.Kind(clip),"link");
if(!ClipWeave.ClipPresentation.Matches(clip,"CLIPER","★","link") || ClipWeave.ClipPresentation.Matches(clip,"","Other","") || ClipWeave.ClipPresentation.Matches(clip,"","Work","image"))throw new Exception("History filters did not intersect");
var bounds=ClipWeave.ClipPresentation.PanelBounds(-1920,0,1920,1040,1.5);
if(bounds.X!=-1902 || bounds.Y!=377 || bounds.Width!=1884 || bounds.Height!=645)throw new Exception("Scaled multi-monitor panel placement failed");
var small=ClipWeave.ClipPresentation.PanelBounds(10,20,300,200,1);
if(small.X<10 || small.Y<20 || small.X+small.Width>310 || small.Y+small.Height>220)throw new Exception("Panel exceeds small work area");
if(!ClipWeave.ClipPresentation.IsSensitiveFormat("ExcludeClipboardContentFromMonitorProcessing") || ClipWeave.ClipPresentation.IsSensitiveFormat("HTML Format"))throw new Exception("Privacy format filtering failed");
Console.WriteLine("History filters, DPI placement and privacy format tests passed");
