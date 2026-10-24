using System.Text.Json.Nodes;
namespace ClipWeave;

/// Bounded layout pages retain every UTF-16 code unit, including surrogate pairs.
public sealed class PreviewText {
    public const int PageSize=16000;
    readonly string text;
    readonly List<int> starts=new(){0};
    public PreviewText(string value) {
        text=value;
        for(var offset=PageSize;offset<value.Length;) {
            if(char.IsLowSurrogate(value[offset])&&char.IsHighSurrogate(value[offset-1]))offset--;
            starts.Add(offset);offset+=PageSize;
        }
    }
    public int Count=>starts.Count;
    public string Page(int index) { index=Math.Clamp(index,0,Count-1);var start=starts[index];var end=index+1<Count?starts[index+1]:text.Length;return text[start..end]; }
    public int Find(string query,int start) => string.IsNullOrEmpty(query)?-1:text.IndexOf(query,Math.Clamp(start,0,text.Length),StringComparison.OrdinalIgnoreCase);
    public int PageAt(int offset){var i=starts.BinarySearch(Math.Clamp(offset,0,text.Length));return i>=0?i:Math.Max(0,~i-1);}
    public int Start(int index)=>starts[Math.Clamp(index,0,Count-1)];
    public static string Content(JsonObject value)=>value["text"]?.GetValue<string>() ?? value["document"]?["text"]?.GetValue<string>() ?? "";
    // A URL inside a document must never replace the full original text.
    public static bool HasOriginalText(JsonObject value)=>value["text"] is JsonValue;
}
