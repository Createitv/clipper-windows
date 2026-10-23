using System.Text;
namespace ClipWeave;
// Only the Go-encrypted envelope is stored here. Its independent key stays in Credential Locker.
public sealed class PendingSessionStore(string directory) {
    readonly string file=Path.Combine(directory,".pending-session.enc");
    public string Read(){if(!File.Exists(file))return "";var size=new FileInfo(file).Length;if(size>128*1024)throw new IOException("Invalid session snapshot size");return File.ReadAllText(file,Encoding.UTF8);}
    public void Quarantine(){if(File.Exists(file))File.Move(file,file+".failed-"+Guid.NewGuid());}
    public void Write(string value){
        if(value.Length==0){File.Delete(file);return;}
        var bytes=Encoding.UTF8.GetBytes(value);if(bytes.Length>128*1024)throw new IOException("Invalid session snapshot size");
        var temporary=file+"."+Guid.NewGuid()+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}File.Move(temporary,file,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
public sealed class SessionRestoration(PendingSessionStore store) {
    public bool Attempted {get;private set;} public bool Failed {get;private set;}
    public async Task Ensure(Func<string,Task> restore){if(Attempted)return;Attempted=true;try{var text=store.Read();if(text.Length>0)await restore(text);}catch{Failed=true;try{store.Quarantine();}catch(IOException){}catch(UnauthorizedAccessException){}}}
    public void Reauthenticated()=>Failed=false;
}
