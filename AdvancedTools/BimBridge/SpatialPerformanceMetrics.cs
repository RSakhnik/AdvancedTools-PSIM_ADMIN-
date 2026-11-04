using System;
using System.Diagnostics;
using System.Threading;
using Pilot.BimBridge.Contracts;

namespace AdvancedTools.BimBridge
{
    public sealed class SpatialPerformanceMetrics
    {
        private readonly bool _detailed;
        public SpatialPerformanceMetrics(bool detailed=false){_detailed=detailed;}
        private long _sources,_nodes,_instances,_unique,_boundsTicks,_traversalTicks,_meshAcquireTicks,_verticesTicks;
        private long _encodingTicks,_artifactWriteTicks,_artifactFinalizeTicks,_modelAccessTicks,_snapshotIndexTicks,_sdkDispatchWaitTicks;
        private long _meshHits,_meshMisses;
        private long _indexHits,_indexMisses,_indexCalls;
        public long SourceElements=>Interlocked.Read(ref _sources);public long TraversedNodes=>Interlocked.Read(ref _nodes);
        public long Instances=>Interlocked.Read(ref _instances);public long UniqueMeshes=>Interlocked.Read(ref _unique);
        public double BoundsCpuMs=>Ms(_boundsTicks);public double SdkTraversalReadMs=>Ms(_traversalTicks);
        public double SdkMeshAcquireMs=>Ms(_meshAcquireTicks);public double SdkVerticesReadMs=>Ms(_verticesTicks);
        public double BinaryEncodingActiveMs=>Ms(_encodingTicks);
        public double ArtifactWriteWallMs=>Ms(_artifactWriteTicks);
        public double ArtifactFinalizeWallMs=>Ms(_artifactFinalizeTicks);
        public double ModelAccessWallMs=>Ms(_modelAccessTicks);
        public double SnapshotIndexWallMs=>Ms(_snapshotIndexTicks);
        public double SdkDispatchWaitWallMs=>Ms(_sdkDispatchWaitTicks);
        private static double Ms(long ticks)=>ticks*1000d/Stopwatch.Frequency;
        public void Source()=>Interlocked.Increment(ref _sources);
        public void Node()=>Interlocked.Increment(ref _nodes);
        public void Instance()=>Interlocked.Increment(ref _instances);
        public void UniqueMesh()=>Interlocked.Increment(ref _unique);
        public void AddBounds(long ticks)=>Interlocked.Add(ref _boundsTicks,ticks);
        public void AddTraversal(long ticks)=>Interlocked.Add(ref _traversalTicks,ticks);
        public void AddMeshAcquire(long ticks)=>Interlocked.Add(ref _meshAcquireTicks,ticks);
        public void AddVertices(long ticks)=>Interlocked.Add(ref _verticesTicks,ticks);
        public void AddEncoding(long ticks)=>Interlocked.Add(ref _encodingTicks,ticks);
        public void AddArtifactWrite(long ticks)=>Interlocked.Add(ref _artifactWriteTicks,ticks);
        public void AddArtifactFinalize(long ticks)=>Interlocked.Add(ref _artifactFinalizeTicks,ticks);
        public void AddModelAccess(long ticks)=>Interlocked.Add(ref _modelAccessTicks,ticks);
        public void AddSdkDispatchWait(long ticks)=>Interlocked.Add(ref _sdkDispatchWaitTicks,ticks);
        public void AddSnapshotIndex(long ticks,bool hit)
        {
            Interlocked.Add(ref _snapshotIndexTicks,ticks);
            Interlocked.Increment(ref _indexCalls);
            if(!_detailed)return;
            if(hit)Interlocked.Increment(ref _indexHits);
            else Interlocked.Increment(ref _indexMisses);
        }
        public void MeshLookup(bool hit)
        {
            if(!_detailed)return;
            if(hit)Interlocked.Increment(ref _meshHits);
            else Interlocked.Increment(ref _meshMisses);
        }
        public void Add(SpatialPerformanceMetrics task)
        {
            if(task==null)return;
            Interlocked.Add(ref _sources,Interlocked.Read(ref task._sources));
            Interlocked.Add(ref _nodes,Interlocked.Read(ref task._nodes));
            Interlocked.Add(ref _instances,Interlocked.Read(ref task._instances));
            Interlocked.Add(ref _unique,Interlocked.Read(ref task._unique));
            Interlocked.Add(ref _boundsTicks,Interlocked.Read(ref task._boundsTicks));
            Interlocked.Add(ref _traversalTicks,Interlocked.Read(ref task._traversalTicks));
            Interlocked.Add(ref _meshAcquireTicks,Interlocked.Read(ref task._meshAcquireTicks));
            Interlocked.Add(ref _verticesTicks,Interlocked.Read(ref task._verticesTicks));
            Interlocked.Add(ref _encodingTicks,Interlocked.Read(ref task._encodingTicks));
            Interlocked.Add(ref _artifactWriteTicks,Interlocked.Read(ref task._artifactWriteTicks));
            Interlocked.Add(ref _artifactFinalizeTicks,Interlocked.Read(ref task._artifactFinalizeTicks));
            Interlocked.Add(ref _modelAccessTicks,Interlocked.Read(ref task._modelAccessTicks));
            Interlocked.Add(ref _snapshotIndexTicks,Interlocked.Read(ref task._snapshotIndexTicks));
            Interlocked.Add(ref _sdkDispatchWaitTicks,Interlocked.Read(ref task._sdkDispatchWaitTicks));
            Interlocked.Add(ref _indexHits,Interlocked.Read(ref task._indexHits));
            Interlocked.Add(ref _indexMisses,Interlocked.Read(ref task._indexMisses));
            Interlocked.Add(ref _indexCalls,Interlocked.Read(ref task._indexCalls));
            Interlocked.Add(ref _meshHits,Interlocked.Read(ref task._meshHits));
            Interlocked.Add(ref _meshMisses,Interlocked.Read(ref task._meshMisses));
        }
        public SpatialTaskPerformanceDto Snapshot(Guid taskId,Guid assignmentId,int attemptNumber)=>new SpatialTaskPerformanceDto
        {
            TaskId=taskId,AssignmentId=assignmentId,AttemptNumber=attemptNumber,
            SourceElements=SourceElements,TraversedNodes=TraversedNodes,Instances=Instances,UniqueMeshes=UniqueMeshes,
            SdkTraversalReadMs=SdkTraversalReadMs,SdkMeshAcquireMs=SdkMeshAcquireMs,
            SdkVerticesReadMs=SdkVerticesReadMs,BoundsCpuMs=BoundsCpuMs,
            BinaryEncodingActiveMs=BinaryEncodingActiveMs,ArtifactWriteWallMs=ArtifactWriteWallMs,
            ArtifactFinalizeWallMs=ArtifactFinalizeWallMs,
            ModelAccessWallMs=ModelAccessWallMs,
            SdkDispatchWaitWallMs=SdkDispatchWaitWallMs,
            SnapshotIndexWallMs=Interlocked.Read(ref _indexCalls)>0?SnapshotIndexWallMs:(double?)null,
            SnapshotIndexHits=_detailed&&Interlocked.Read(ref _indexCalls)>0?Interlocked.Read(ref _indexHits):(long?)null,
            SnapshotIndexMisses=_detailed&&Interlocked.Read(ref _indexCalls)>0?Interlocked.Read(ref _indexMisses):(long?)null,
            MeshCacheHits=_detailed?Interlocked.Read(ref _meshHits):(long?)null,
            MeshCacheMisses=_detailed?Interlocked.Read(ref _meshMisses):(long?)null
        };
    }
}
