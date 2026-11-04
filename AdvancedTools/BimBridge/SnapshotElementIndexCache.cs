using Ascon.Pilot.Bim.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    // Worker-owned snapshot data. SDK objects and enumerators never leave ReadAsync.
    public sealed class SnapshotElementIndexCache
    {
        public sealed class PartIndex
        {
            private readonly HashSet<Guid> _ids;
            public Guid[] OrderedIds {get;}
            internal PartIndex(Guid[] ordered) {OrderedIds=ordered;_ids=new HashSet<Guid>(ordered);}
            public bool Contains(Guid id)=>_ids.Contains(id);
            internal long EstimatedBytes=>128L+OrderedIds.LongLength*48L;
        }
        private sealed class Entry
        {
            public string Key;public PartIndex Index;public DateTime LastUseUtc;
        }
        private readonly Dictionary<string,LinkedListNode<Entry>> _entries=new Dictionary<string,LinkedListNode<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru=new LinkedList<Entry>();
        private readonly SemaphoreSlim _serial=new SemaphoreSlim(1,1);
        private readonly long _maxBytes;private readonly int _maxEntries;
        private long _bytes,_hits,_misses,_builds,_evictions;
        public SnapshotElementIndexCache(long maxBytes,int maxEntries){_maxBytes=maxBytes;_maxEntries=maxEntries;}
        public long Hits=>Interlocked.Read(ref _hits);public long Misses=>Interlocked.Read(ref _misses);public long Builds=>Interlocked.Read(ref _builds);
        public long Evictions=>Interlocked.Read(ref _evictions);
        public long Bytes=>Interlocked.Read(ref _bytes);public int Entries=>_entries.Count;
        private static string Key(ModelAccess model,Guid part)=>model.ModelId.ToString("N")+"/"+model.Version.ToUniversalTime().Ticks+"/"+part.ToString("N");
        public async Task<PartIndex> GetAsync(ModelAccess model,Guid part,CancellationToken token,SpatialPerformanceMetrics performance=null)
        {
            var started=performance==null?0:Stopwatch.GetTimestamp();
            var cacheHit=false;
            try
            {
            var key=Key(model,part);
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(_entries.TryGetValue(key,out var hit))
                {
                    cacheHit=true;
                    Interlocked.Increment(ref _hits);hit.Value.LastUseUtc=DateTime.UtcNow;_lru.Remove(hit);_lru.AddFirst(hit);return hit.Value.Index;
                }
                Interlocked.Increment(ref _misses);
                // Materialize plain IDs in bounded dispatcher slices. No SDK cursor
                // escapes its owning dispatcher; it is released through SDK cleanup.
                var ids=new List<Guid>();
                var cursor=await model.Sdk.ReadAsync(storage=>
                    new SpatialSdkCursor<IModelElement>(storage.LoadElements(part,model.Version)),token).ConfigureAwait(false);
                try
                {
                    while(true)
                    {
                        var batch=await model.Sdk.ReadAsync(storage=>cursor.Read(64,token).Select(item=>
                        {
                            var identity=item.Id;
                            if(identity.ModelPartId!=part||identity.ElementId==Guid.Empty)
                                throw new BimBridgeException("INVALID_SPATIAL_DATA","Snapshot index contains an invalid element identity.");
                            return identity.ElementId;
                        }).ToArray(),token).ConfigureAwait(false);
                        ids.AddRange(batch);
                        if(ids.Count>(_maxBytes-128L)/48L)
                            throw new BimBridgeException("RESOURCE_LIMIT","Snapshot part index exceeds configured memory budget.");
                        if(batch.Length<64)break;
                    }
                }
                finally{await model.Sdk.ReleaseOwnedAsync(cursor).ConfigureAwait(false);}
                var ordered=ids.ToArray();
                var index=new PartIndex(ordered);Interlocked.Increment(ref _builds);
                if(index.EstimatedBytes>_maxBytes||_maxEntries<1)
                    throw new BimBridgeException("RESOURCE_LIMIT","Snapshot part index exceeds configured memory budget.");
                while(_entries.Count>=_maxEntries||_bytes>_maxBytes-index.EstimatedBytes)
                {var last=_lru.Last;_entries.Remove(last.Value.Key);_bytes-=last.Value.Index.EstimatedBytes;_lru.RemoveLast();Interlocked.Increment(ref _evictions);}
                var entry=new LinkedListNode<Entry>(new Entry{Key=key,Index=index,LastUseUtc=DateTime.UtcNow});
                _lru.AddFirst(entry);_entries.Add(key,entry);Interlocked.Exchange(ref _bytes,_bytes+index.EstimatedBytes);
                return index;
            }
            finally{_serial.Release();}
            }
            finally{if(performance!=null)performance.AddSnapshotIndex(Stopwatch.GetTimestamp()-started,cacheHit);}
        }
    }
}
