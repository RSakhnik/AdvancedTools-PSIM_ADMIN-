using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    // SDK -> writer -> optional compressor -> hash/count -> durable bytes.
    // Unlike the legacy query writer, exports do not reread a JSON file to gzip
    // and then reread it again to hash it.
    public sealed class ArtifactFileWriter
    {
        private readonly string _directory;
        private readonly BimBridgeSettings _settings;
        public ArtifactFileWriter(string directory,BimBridgeSettings settings) {_directory=directory;_settings=settings;}
        public async Task<PreparedResult> WriteAsync(Guid taskId,string contentType,string fileName,bool gzip,
            Func<Stream,CancellationToken,Task> write,CancellationToken token)
        {
            Directory.CreateDirectory(_directory);
            var path=Path.Combine(_directory,Guid.NewGuid().ToString("N")+".artifact.tmp");
            try
            {
                string hash;long bytes;
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
                using(var digest=SHA256.Create())
                using(var counted=new ArtifactHashStream(file,digest,_settings.MaxArtifactBytes,token))
                {
                    if(gzip)
                    {
                        using(var compressor=new GZipStream(counted,CompressionLevel.Optimal,true))
                            await write(compressor,token).ConfigureAwait(false);
                    }
                    else await write(counted,token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();counted.Flush();file.Flush(true);
                    digest.TransformFinalBlock(Array.Empty<byte>(),0,0);
                    hash=BitConverter.ToString(digest.Hash).Replace("-","").ToLowerInvariant();bytes=counted.Written;
                }
                return new PreparedResult {Path=path,Length=bytes,Sha256=hash,ContentType=contentType,
                    ContentEncoding=gzip?"gzip":"identity",FileName=fileName,ResultKind="artifact",DeleteOnDispose=true};
            }
            catch {TemporaryFileCleaner.TryDelete(path);throw;}
        }
    }
    internal sealed class ArtifactHashStream:Stream
    {
        private readonly Stream _target;private readonly HashAlgorithm _hash;private readonly long _maximum;private readonly CancellationToken _token;
        public long Written {get;private set;}
        public ArtifactHashStream(Stream target,HashAlgorithm hash,long maximum,CancellationToken token) {_target=target;_hash=hash;_maximum=maximum;_token=token;}
        public override void Write(byte[] buffer,int offset,int count)
        {
            _token.ThrowIfCancellationRequested();
            if(Written>_maximum-count) throw new BimBridgeException("RESULT_TOO_LARGE","Artifact exceeds its configured byte limit.");
            _target.Write(buffer,offset,count);_hash.TransformBlock(buffer,offset,count,null,0);Written+=count;
        }
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>Written;public override long Position {get=>Written;set=>throw new NotSupportedException();}
        public override void Flush()=>_target.Flush();
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
    }
}
