using System.Text;
namespace ClipWeave;
// The pipe is serial, but a read can contain part of a reply or several lines.
// Keep the unread tail and bound each reply without a character-per-await loop.
public sealed class BoundedLineReader(TextReader source) {
    readonly char[] buffer=new char[8192];
    int offset,count;
    public async Task<string> ReadAsync(int limit,CancellationToken token) {
        var output=new StringBuilder();
        while(true) {
            if(offset==count) {
                count=await source.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false);offset=0;
                if(count==0)throw new EndOfStreamException(L10n.Text("Go 核心已退出"));
            }
            var newline=Array.IndexOf(buffer,'\n',offset,count-offset);
            var length=(newline<0?count:newline)-offset;
            if(output.Length+length>limit)throw new IOException(L10n.Text("Go 核心响应过大"));
            output.Append(buffer,offset,length);offset+=length;
            if(newline>=0){offset++;return output.ToString().TrimEnd('\r');}
        }
    }
}
