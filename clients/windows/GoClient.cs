using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace ClipWeave;
public sealed class BridgeException(string message,JsonObject? payload) : IOException(L10n.Text(message)) { public JsonObject? Payload {get;}=payload; }
public sealed class GoClient : IAsyncDisposable {
    readonly Process process;
    readonly SemaphoreSlim serial = new(1,1);
    public GoClient() {
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClipWeave","vault");
        Directory.CreateDirectory(directory);
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"clipweave-core.exe")) {
            UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8
        };
        start.ArgumentList.Add("--vault-dir"); start.ArgumentList.Add(directory);
        process=Process.Start(start) ?? throw new IOException(L10n.Text("无法启动 Go 核心"));
        // Drain diagnostics without retaining potentially sensitive process output.
        process.ErrorDataReceived += (_,_)=>{}; process.BeginErrorReadLine();
    }
    public async Task<JsonNode?> Call(string action, JsonObject? args=null) {
        await serial.WaitAsync();
        try {
            var id=Guid.NewGuid().ToString(); args ??= new();
            if(action=="requireAccount") {
                string model="Windows PC";
                try { using var bios=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"); model=bios?.GetValue("SystemProductName")?.ToString()??model; } catch(System.Security.SecurityException) { }
                args["deviceInfo"]=new JsonObject{["name"]=Environment.MachineName,["model"]=model,["platform"]="windows",["osVersion"]=System.Runtime.InteropServices.RuntimeInformation.OSDescription};
            } args["id"]=id; args["action"]=action;
            await process.StandardInput.WriteLineAsync(args.ToJsonString()); await process.StandardInput.FlushAsync();
            // A lost response terminates the pipe so stale replies cannot be mistaken for new requests.
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string raw;
            try { raw=await ReadBoundedLine(process.StandardOutput,64*1024*1024,timeout.Token); }
            catch { if(!process.HasExited) process.Kill(); throw; }
            var response=JsonNode.Parse(raw)?.AsObject() ?? throw new IOException(L10n.Text("Go 核心返回了无效响应"));
            if(response["id"]?.GetValue<string>()!=id) throw new IOException(L10n.Text("Go 核心响应序号不匹配"));
            if(response["ok"]?.GetValue<bool>()!=true) throw new BridgeException(response["error"]?.GetValue<string>() ?? L10n.Text("操作失败"),response["data"]?.DeepClone() as JsonObject);
            return response["data"]?.DeepClone();
        } finally { serial.Release(); }
    }
    static async Task<string> ReadBoundedLine(StreamReader reader,int limit,CancellationToken token) {
        var output=new StringBuilder(); var one=new char[1];
        while(true) {
            if(await reader.ReadAsync(one.AsMemory(),token)==0) throw new EndOfStreamException(L10n.Text("Go 核心已退出"));
            if(one[0]=='\n') return output.ToString();
            if(output.Length>=limit) throw new IOException(L10n.Text("Go 核心响应过大"));
            output.Append(one[0]);
        }
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
