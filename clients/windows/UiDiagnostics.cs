namespace ClipWeave;
// Only exception type, HRESULT and code frames; never clipboard/account payloads.
static class UiDiagnostics {
    public static void Stage(string marker){try{var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipWeave");Directory.CreateDirectory(directory);var path=Path.Combine(directory,"ui-startup.log");if(File.Exists(path)&&new FileInfo(path).Length>65536)File.Delete(path);File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {marker}\n");}catch{}}
    public static void Record(string operation,Exception error){
        try{var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipWeave");Directory.CreateDirectory(directory);var path=Path.Combine(directory,"ui-errors.log");if(File.Exists(path)&&new FileInfo(path).Length>65536)File.Delete(path);File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {operation} {error.GetType().FullName} 0x{error.HResult:X8}\n{error.StackTrace}\n");}catch{}
    }
}
