using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.Rendering;

namespace ClipWeave;

// The QR contains the public 12-character locator only. All authorization and
// encrypted key transfer still run through the shared Go pairing protocol.
public static class AuthorizationCode {
    public static string? Normalize(string? value) {
        var code=new string((value ?? "").Where(c=>!char.IsWhiteSpace(c)&&c!='-').ToArray()).ToUpperInvariant();
        return code.Length==12&&code.All(c=>"ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c))?code:null;
    }
    public static PixelData Encode(string code) {
        code=Payload(code) ?? throw new ArgumentException("无效的设备授权二维码");
        return new BarcodeWriterPixelData {
            Format=BarcodeFormat.QR_CODE,
            Options=new QrCodeEncodingOptions { Width=232,Height=232,Margin=4,ErrorCorrection=ZXing.QrCode.Internal.ErrorCorrectionLevel.M }
        }.Write(code);
    }
    public static string? Decode(byte[] pixels,int width,int height) {
        var reader=new BarcodeReaderGeneric { AutoRotate=true,Options=new DecodingOptions { TryHarder=true,PossibleFormats=new[]{BarcodeFormat.QR_CODE} } };
        return Payload(reader.Decode(pixels,width,height,RGBLuminanceSource.BitmapFormat.BGRA32)?.Text);
    }
    public static string? Payload(string? value) {
        if(value==null)return null;
        if(Normalize(value) is string code)return code; // Older code-only QR: inspect manually.
        var parts=value.Trim().Split(':');
        if(parts.Length!=3 || parts[0]!="cliper-pair-v1" || Normalize(parts[1]) is not string normalized || parts[2].Length!=16 || !parts[2].All(Uri.IsHexDigit))return null;
        return $"cliper-pair-v1:{normalized}:{parts[2].ToUpperInvariant()}";
    }
}
