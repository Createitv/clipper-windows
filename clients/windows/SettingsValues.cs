using System.Globalization;
using System.Text.Json.Nodes;
namespace ClipWeave;
public static class SettingsValues {
    public static bool Boolean(JsonObject value,string key,bool fallback=false)=>bool.TryParse(value[key]?.ToString(),out var flag)?flag:fallback;
    public static int RetentionDays(JsonObject value)=>double.TryParse(value["retentionDays"]?.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var days)&&days is 0 or 7 or 30 or 90?(int)days:0;
}
