using System.Text.Json;
namespace ClipWeave;
sealed class WindowsPreferences {
    public bool Pinned {get;set;}
    public bool ShowTray {get;set;}=true;
    public HashSet<string> AppliedDefaults {get;set;}=[];
    static string FilePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipWeave","windows-preferences.json");
    public static WindowsPreferences Load(){try{var value=JsonSerializer.Deserialize<WindowsPreferences>(File.ReadAllText(FilePath))??new();value.AppliedDefaults??=[];return value;}catch{return new();}}
    public void Save(){Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);var temporary=FilePath+"."+Guid.NewGuid()+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(this));File.Move(temporary,FilePath,true);}
}
