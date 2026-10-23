using System.Text;
namespace ClipWeave;
// Read the pipe bytes directly. TextReader.ReadAsync may wait to fill its
// requested character buffer even when a complete newline-terminated reply
// has already arrived at an exact transport-buffer boundary.
public sealed class BoundedLineReader(Stream source) {
    readonly byte[] buffer=new byte[8192];
    static readonly UTF8Encoding utf8=new(false,true);
    int offset,count;
    public async Task<string> ReadAsync(int limit,CancellationToken token) {
        using var output=new MemoryStream();
        while(true) {
            token.ThrowIfCancellationRequested();
            if(offset==count) {
                count=await source.ReadAsync(buffer.AsMemory(),token).ConfigureAwait(false);offset=0;
                if(count==0)throw new EndOfStreamException(L10n.Text("Go 核心已退出"));
            }
            var newline=Array.IndexOf(buffer,(byte)'\n',offset,count-offset);
            var length=(newline<0?count:newline)-offset;
            if(output.Length+length>limit)throw new IOException(L10n.Text("Go 核心响应过大"));
            output.Write(buffer,offset,length);offset+=length;
            if(newline>=0){offset++;return utf8.GetString(output.GetBuffer(),0,checked((int)output.Length)).TrimEnd('\r');}
        }
    }
}
