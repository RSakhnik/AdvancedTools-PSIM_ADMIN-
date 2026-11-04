using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AdvancedTools.BimBridge
{
    // Plain CLR arrays only. A Pilot mesh object is bound to its SDK dispatcher
    // and must never be retained across tasks or used by the uploader.
    public sealed class MeshGeometryCache
    {
        public sealed class Geometry
        {
            public List<double[]> Vertices,Normals;
            public List<uint[]> Indices,Edges;
            public Geometry Clone()=>new Geometry{Vertices=Vertices,Normals=Normals,Indices=Indices,Edges=Edges};
            public long Bytes=>8L*((Vertices?.Sum(x=>x.LongLength)??0)+(Normals?.Sum(x=>x.LongLength)??0))+
                4L*((Indices?.Sum(x=>x.LongLength)??0)+(Edges?.Sum(x=>x.LongLength)??0));
            public bool Has(bool normals,bool indices,bool edges)=>Vertices!=null&&(!normals||Normals!=null)&&(!indices||Indices!=null)&&(!edges||Edges!=null);
        }
        public struct MeshKey:IEquatable<MeshKey>
        {
            public Guid Model,Part,Mesh;public long VersionTicks;
            public bool Equals(MeshKey other)=>Model==other.Model&&Part==other.Part&&Mesh==other.Mesh&&VersionTicks==other.VersionTicks;
            public override bool Equals(object value)=>value is MeshKey other&&Equals(other);
            public override int GetHashCode(){unchecked{var h=Model.GetHashCode();h=h*31+Part.GetHashCode();h=h*31+Mesh.GetHashCode();return h*31+VersionTicks.GetHashCode();}}
        }
        private sealed class Entry {public MeshKey Key;public Geometry Value;public long Bytes;}
        private readonly Dictionary<MeshKey,LinkedListNode<Entry>> _entries=new Dictionary<MeshKey,LinkedListNode<Entry>>();
        private readonly LinkedList<Entry> _lru=new LinkedList<Entry>();
        private readonly object _gate=new object();private readonly long _maxBytes;private readonly int _maxEntries;
        private long _bytes,_hits,_misses,_evictions;
        public MeshGeometryCache(long maxBytes,int maxEntries){_maxBytes=maxBytes;_maxEntries=maxEntries;}
        public long Bytes{get{lock(_gate)return _bytes;}}public int Entries{get{lock(_gate)return _entries.Count;}}
        public long Hits=>Interlocked.Read(ref _hits);public long Misses=>Interlocked.Read(ref _misses);
        public long Evictions=>Interlocked.Read(ref _evictions);
        public long MaxEntryBytes=>_maxBytes/4; // A single definition cannot monopolize the worker cache.
        public static MeshKey Key(ModelAccess model,Guid part,Guid mesh)=>new MeshKey{
            Model=model.ModelId,VersionTicks=model.Version.ToUniversalTime().Ticks,Part=part,Mesh=mesh};
        public Geometry Find(MeshKey key,bool normals,bool indices,bool edges)
        {
            lock(_gate)
            {
                if(!_entries.TryGetValue(key,out var item)) {Interlocked.Increment(ref _misses);return null;}
                _lru.Remove(item);_lru.AddFirst(item);
                if(item.Value.Value.Has(normals,indices,edges))Interlocked.Increment(ref _hits);
                else Interlocked.Increment(ref _misses);
                return item.Value.Value;
            }
        }
        public void Put(MeshKey key,Geometry geometry)
        {
            var size=geometry.Bytes;if(size>MaxEntryBytes||size>_maxBytes||_maxEntries<1)return;
            lock(_gate)
            {
                if(_entries.TryGetValue(key,out var existing))
                {_bytes-=existing.Value.Bytes;_entries.Remove(key);_lru.Remove(existing);}
                while(_entries.Count>=_maxEntries||_bytes>_maxBytes-size)
                {var last=_lru.Last;_entries.Remove(last.Value.Key);_bytes-=last.Value.Bytes;_lru.RemoveLast();Interlocked.Increment(ref _evictions);}
                var node=new LinkedListNode<Entry>(new Entry{Key=key,Value=geometry,Bytes=size});
                _entries.Add(key,node);_lru.AddFirst(node);_bytes+=size;
            }
        }
    }
}
