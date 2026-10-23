using System.Text.RegularExpressions;
namespace ClipWeave;
static partial class L10n {
    internal static string ServiceStatus(string raw)=>ChineseErrors.Keys.Any(k=>raw.Contains(k,StringComparison.Ordinal))||Regex.IsMatch(raw,@"HTTP[ :]*[45][0-9][0-9]",RegexOptions.IgnoreCase)?Error(raw):Text(raw);
    internal static string Error(string raw){
        foreach(var key in ChineseErrors.Keys.OrderByDescending(k=>k.Length))if(Regex.IsMatch(raw,"(?<![A-Za-z0-9_])"+Regex.Escape(key)+"(?![A-Za-z0-9_])"))return Text(key);
        if(Translations.ContainsKey(raw)||ChineseErrors.ContainsValue(raw)||Translations.ContainsValue(raw))return Text(raw);
        if(Regex.IsMatch(raw,"timeout|timed out|failed to connect|connection refused|network is unreachable|TLS handshake",RegexOptions.IgnoreCase))return Text("network_error");
        return Text("请求失败");
    }
}
