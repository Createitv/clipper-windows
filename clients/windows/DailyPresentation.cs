using System.Globalization;
using System.Text.Json.Nodes;
namespace ClipWeave;
public static class WindowsLanguage {
    static readonly CultureInfo systemCulture=CultureInfo.CurrentUICulture;
    public static string Resolve(string selection,string system)=>selection is "zh-Hans" or "en"?selection:system.Replace('_','-').StartsWith("zh",StringComparison.OrdinalIgnoreCase)?"zh-Hans":"en";
    public static void Apply(string selection){var culture=CultureInfo.GetCultureInfo(Resolve(selection,systemCulture.Name)=="en"?"en-US":"zh-CN");CultureInfo.CurrentUICulture=culture;CultureInfo.CurrentCulture=culture;CultureInfo.DefaultThreadCurrentUICulture=culture;CultureInfo.DefaultThreadCurrentCulture=culture;}
}
public static class DevicePresentation {
    public static string Platform(JsonObject info)=>info["platform"]?.ToString()?.ToLowerInvariant() switch{"ios" or "iphone"=>"iPhone","ipados" or "ipad"=>"iPad","macos" or "mac"=>"Mac","windows"=>"Windows","harmonyos"=>"HarmonyOS","web"=>L10n.Text("浏览器"),_=>info["platform"]?.ToString()??L10n.Text("设备")};
    public static string Glyph(JsonObject info)=>info["platform"]?.ToString()?.ToLowerInvariant() switch{"macos" or "mac" or "windows" or "linux"=>"\uE770","ipados" or "ipad"=>"\uE70A","web"=>"\uE774",_=>"\uE8EA"};
    public static string Name(JsonObject info)=>info["name"]?.ToString() is string name&&name.Length>0?name:info["model"]?.ToString() is string model&&model.Length>0?model:Platform(info);
    public static string Summary(JsonObject info)=>string.Join(" · ",new[]{Platform(info),info["model"]?.ToString()??"",info["osVersion"]?.ToString()??""}.Where(x=>x.Length>0).Distinct());
    public static string Time(JsonNode? value){if(!long.TryParse(value?.ToString(),out var ms)||ms<=0)return "—";try{return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("g",CultureInfo.CurrentCulture);}catch{return "—";}}
}
public sealed class ImportItem(string name,string path,bool folder=false) {
    public string Name {get;}=name; public string Path {get;}=path;public bool Folder {get;}=folder;
    public string? ClipId {get;set;} public string Error {get;set;}="";public bool Complete {get;set;}
}
public sealed class ImportBatch(IReadOnlyList<ImportItem> items,string group) {
    public IReadOnlyList<ImportItem> Items {get;}=items;public string Group {get;}=group;
    public async Task Save(Func<string,JsonObject,Task<JsonNode?>> call){
        foreach(var item in Items.Where(x=>!x.Complete)){
            try{if(item.ClipId==null){var clip=await call(item.Folder?"importFolder":"importFile",new(){["path"]=item.Path});item.ClipId=clip?["id"]?.ToString()??throw new IOException(L10n.Text("Go 核心返回了无效响应"));}
                if(Group.Length>0)await call("group",new(){["clipId"]=item.ClipId,["name"]=Group});item.Complete=true;item.Error="";
            }catch(Exception e){item.Error=e.Message;}
        }
    }
}
