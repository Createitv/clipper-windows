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
var clip=new System.Text.Json.Nodes.JsonObject{["type"]="text",["text"]="Design https://cliper.dev/features",["group"]="Work",["favourite"]=true};
Equal(ClipWeave.ClipPresentation.Kind(clip),"link");
if(!ClipWeave.ClipPresentation.Matches(clip,"CLIPER","★","link") || ClipWeave.ClipPresentation.Matches(clip,"","Other","") || ClipWeave.ClipPresentation.Matches(clip,"","Work","image"))throw new Exception("History filters did not intersect");
var bounds=ClipWeave.ClipPresentation.PanelBounds(-1920,0,1920,1040,1.5);
if(bounds.X!=-1902 || bounds.Y!=377 || bounds.Width!=1884 || bounds.Height!=645)throw new Exception("Scaled multi-monitor panel placement failed");
var small=ClipWeave.ClipPresentation.PanelBounds(10,20,300,200,1);
if(small.X<10 || small.Y<20 || small.X+small.Width>310 || small.Y+small.Height>220)throw new Exception("Panel exceeds small work area");
if(!ClipWeave.ClipPresentation.IsSensitiveFormat("ExcludeClipboardContentFromMonitorProcessing") || ClipWeave.ClipPresentation.IsSensitiveFormat("HTML Format"))throw new Exception("Privacy format filtering failed");
Console.WriteLine("History filters, DPI placement and privacy format tests passed");

var shortPanel=ClipWeave.ClipPresentation.PanelBounds(-1920,0,1920,1040,1.5,360);
var tallPanel=ClipWeave.ClipPresentation.PanelBounds(-1920,0,1920,1040,1.5,620);
if(shortPanel.Y+shortPanel.Height!=tallPanel.Y+tallPanel.Height||tallPanel.Height<=shortPanel.Height)throw new Exception("Height adjustment must preserve the bottom anchor");
var limited=ClipWeave.ClipPresentation.PanelBounds(0,0,800,480,2,900);
if(limited.Y<0||limited.Y+limited.Height>480)throw new Exception("Resized panel leaves work area");
var legacy=System.Text.Json.Nodes.JsonNode.Parse("{\"captureEnabled\":\"true\",\"retentionDays\":30.0}")!.AsObject();
if(!ClipWeave.SettingsValues.Boolean(legacy,"captureEnabled")||ClipWeave.SettingsValues.RetentionDays(legacy)!=30)throw new Exception("Legacy settings must remain readable");
if(ClipWeave.SettingsValues.RetentionDays(new(){["retentionDays"]="bad"})!=0)throw new Exception("Malformed retention setting must have a safe default");
var snapshots=new System.Collections.ObjectModel.ObservableCollection<System.Text.Json.Nodes.JsonObject>();
var changes=new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
snapshots.CollectionChanged+=(_,e)=>changes.Add(e.Action);
var first=new System.Text.Json.Nodes.JsonObject{["id"]="a",["text"]="first",["version"]=1};
var second=new System.Text.Json.Nodes.JsonObject{["id"]="b",["text"]="second",["version"]=1};
ClipWeave.HistoryUpdates.Apply(snapshots,new[]{first,second});changes.Clear();
ClipWeave.HistoryUpdates.Apply(snapshots,new[]{first.DeepClone().AsObject(),second.DeepClone().AsObject()});
if(changes.Count!=0||!ReferenceEquals(snapshots[0],first))throw new Exception("Unchanged background poll must preserve items and emit no collection events");
var inserted=new System.Text.Json.Nodes.JsonObject{["id"]="c",["text"]="new"};
ClipWeave.HistoryUpdates.Apply(snapshots,new[]{inserted,first,second});
if(changes.Count!=1||changes[0]!=System.Collections.Specialized.NotifyCollectionChangedAction.Add||!ReferenceEquals(snapshots[1],first))throw new Exception("New clips must not reset existing cards");
changes.Clear();var revised=first.DeepClone().AsObject();revised["version"]=2;
ClipWeave.HistoryUpdates.Apply(snapshots,new[]{inserted,revised,second});
if(changes.Count!=1||changes[0]!=System.Collections.Specialized.NotifyCollectionChangedAction.Replace)throw new Exception("A changed clip must replace only its own card");
changes.Clear();ClipWeave.HistoryUpdates.Apply(snapshots,new[]{second,revised});
if(changes.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Reset)||snapshots[0]["id"]?.ToString()!="b")throw new Exception("Reordering and removal must avoid collection resets");
using var source=new CountingReader(new string('x',200_000)+"\nnext\n");
var lines=new ClipWeave.BoundedLineReader(source);
if((await lines.ReadAsync(300_000,CancellationToken.None)).Length!=200_000||await lines.ReadAsync(300_000,CancellationToken.None)!="next")throw new Exception("Chunked replies lost data across message boundaries");
if(source.ReadCalls>30)throw new Exception("Large replies must not read one character at a time");
try{await new ClipWeave.BoundedLineReader(new StringReader("123456\n")).ReadAsync(5,CancellationToken.None);throw new Exception("Oversized reply was accepted");}catch(IOException){}
Console.WriteLine("Incremental refresh, legacy settings, buffered IPC and bottom-anchored height tests passed");
sealed class CountingReader(string content):StringReader(content){
    public int ReadCalls;
    public override ValueTask<int> ReadAsync(Memory<char> buffer,CancellationToken token=default){ReadCalls++;return base.ReadAsync(buffer,token);}
}
