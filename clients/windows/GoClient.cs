using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace ClipWeave;
public sealed class BridgeException(string message,JsonObject? payload) : IOException(L10n.Text(message)) { public JsonObject? Payload {get;}=payload; }
public sealed class GoClient : IAsyncDisposable {
    readonly Process process;
    readonly BoundedLineReader replies;
    readonly SemaphoreSlim serial = new(1,1);
    readonly PendingSessionStore pending;
    readonly SessionRestoration restoration;
    static readonly HashSet<string> SessionActions=["passwordConnect","autoConnect","completePairing","importRecovery","cancelPairing","renewPairing","forgetPendingSession","disconnect","requestAccountDeletion"];
    public GoClient() {
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipWeave","vault");
        Directory.CreateDirectory(directory);pending=new PendingSessionStore(directory);restoration=new SessionRestoration(pending);
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"clipweave-core.exe")) {
            UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8
        };
        start.ArgumentList.Add("--vault-dir"); start.ArgumentList.Add(directory);
        process=Process.Start(start) ?? throw new IOException(L10n.Text("无法启动 Go 核心"));
        replies=new BoundedLineReader(process.StandardOutput.BaseStream);
        // Drain diagnostics without retaining potentially sensitive process output.
        process.ErrorDataReceived += (_,_)=>{}; process.BeginErrorReadLine();
    }
    public Task<JsonNode?> Call(string action,JsonObject? args=null)=>Task.Run(()=>CallInBackground(action,args));
    async Task<JsonNode?> CallInBackground(string action,JsonObject? args) {
        await serial.WaitAsync();
        try {
            await restoration.Ensure(async saved=>{await Invoke("restorePendingSession",new(){["text"]=saved,["passphrase"]=DeviceSecret.PendingKey()});});
            JsonNode? result=null;Exception? failure=null;
            try{result=await Invoke(action,args);}catch(Exception e){failure=e;}
            if(SessionActions.Contains(action)){
                try{if(failure==null&&(action is "forgetPendingSession" or "disconnect" or "requestAccountDeletion")){pending.Write("");restoration.Reauthenticated();}else{if(restoration.Failed&&(action is "passwordConnect" or "autoConnect"))DeviceSecret.ResetPendingKey();var saved=await Invoke("exportPendingSession",new(){["passphrase"]=DeviceSecret.PendingKey()});if(!restoration.Failed||saved?.GetValue<string>() is string envelope&&envelope.Length>0||failure==null){pending.Write(saved?.GetValue<string>()??"");if(failure==null)restoration.Reauthenticated();}}}
                catch(Exception checkpoint){var payload=(failure as BridgeException)?.Payload??result as JsonObject;throw new BridgeException((failure==null?"":failure.Message+"; ")+checkpoint.Message,payload);}
            }
            if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            if(restoration.Failed&&(action is "status" or "requireAccount")&&result is JsonObject snapshot)snapshot["sessionRestoreNotice"]=L10n.Text("待处理会话无法恢复，请重新验证邮箱。");
            return result;
        } finally { serial.Release(); }
    }
    async Task<JsonNode?> Invoke(string action,JsonObject? args) {
            var id=Guid.NewGuid().ToString(); args=args?.DeepClone().AsObject()??new();
 args["id"]=id; args["action"]=action;
            await process.StandardInput.WriteLineAsync(args.ToJsonString()); await process.StandardInput.FlushAsync();
            // A lost response terminates the pipe so stale replies cannot be mistaken for new requests.
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string raw;
            try { raw=await replies.ReadAsync(64*1024*1024,timeout.Token); }
            catch { if(!process.HasExited) process.Kill(); throw; }
            var response=JsonNode.Parse(raw)?.AsObject() ?? throw new IOException(L10n.Text("Go 核心返回了无效响应"));
            if(response["id"]?.GetValue<string>()!=id) throw new IOException(L10n.Text("Go 核心响应序号不匹配"));
            if(response["ok"]?.GetValue<bool>()!=true) throw new BridgeException(response["error"]?.GetValue<string>() ?? L10n.Text("操作失败"),response["data"]?.DeepClone() as JsonObject);
            return response["data"]?.DeepClone();

    }

    public async ValueTask DisposeAsync() {
        await serial.WaitAsync();
        try {
            process.StandardInput.Close();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { process.Kill(); }
            process.Dispose();
        } finally { serial.Release(); serial.Dispose(); }
    }
}
