using System;
using System.Diagnostics;
using System.Threading;

namespace AdvancedTools.BimBridge
{
    public sealed class SpatialPerformanceMetrics
    {
        private long _sources,_nodes,_instances,_unique,_boundsTicks,_traversalTicks,_meshAcquireTicks,_verticesTicks;
        public long SourceElements=>Interlocked.Read(ref _sources);public long TraversedNodes=>Interlocked.Read(ref _nodes);
        public long Instances=>Interlocked.Read(ref _instances);public long UniqueMeshes=>Interlocked.Read(ref _unique);
        public double BoundsCpuMs=>Ms(_boundsTicks);public double SdkTraversalReadMs=>Ms(_traversalTicks);
        public double SdkMeshAcquireMs=>Ms(_meshAcquireTicks);public double SdkVerticesReadMs=>Ms(_verticesTicks);
        private static double Ms(long ticks)=>ticks*1000d/Stopwatch.Frequency;
        public void Source()=>Interlocked.Increment(ref _sources);
        public void Node()=>Interlocked.Increment(ref _nodes);
        public void Instance()=>Interlocked.Increment(ref _instances);
        public void UniqueMesh()=>Interlocked.Increment(ref _unique);
        public void AddBounds(long ticks)=>Interlocked.Add(ref _boundsTicks,ticks);
        public void AddTraversal(long ticks)=>Interlocked.Add(ref _traversalTicks,ticks);
        public void AddMeshAcquire(long ticks)=>Interlocked.Add(ref _meshAcquireTicks,ticks);
        public void AddVertices(long ticks)=>Interlocked.Add(ref _verticesTicks,ticks);
    }
}
