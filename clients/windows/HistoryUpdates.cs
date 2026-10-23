using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
namespace ClipWeave;
public static class HistoryUpdates {
    // Preserve object identity for unchanged cards and never issue a Reset.
    public static void Apply(ObservableCollection<JsonObject> current,IReadOnlyList<JsonObject> incoming) {
        var retained=incoming.Select(x=>x["id"]?.ToString()??"").ToHashSet(StringComparer.Ordinal);
        for(var i=current.Count-1;i>=0;i--)if(!retained.Contains(current[i]["id"]?.ToString()??""))current.RemoveAt(i);
        for(var i=0;i<incoming.Count;i++) {
            var next=incoming[i];var id=next["id"]?.ToString()??"";
            if(i>=current.Count){current.Add(next);continue;}
            if(current[i]["id"]?.ToString()!=id) {
                var existing=-1;for(var j=i+1;j<current.Count;j++)if(current[j]["id"]?.ToString()==id){existing=j;break;}
                if(existing<0){current.Insert(i,next);continue;}
                current.Move(existing,i);
            }
            if(!JsonNode.DeepEquals(current[i],next))current[i]=next;
        }
    }
}
