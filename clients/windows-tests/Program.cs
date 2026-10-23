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
