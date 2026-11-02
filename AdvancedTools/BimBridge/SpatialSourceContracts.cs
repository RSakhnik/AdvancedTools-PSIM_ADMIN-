using System;
using System.Threading;
using System.Threading.Tasks;
namespace AdvancedTools.BimBridge
{
    public sealed class SpatialInstance
    {
        public Guid MeshId { get; set; }
        public Guid MeshPartId { get; set; }
        public double[] Placement { get; set; }
    }

    // This source abstraction permits deterministic traversal/serialization tests
    // without opening Pilot or weakening the production coordinate verification.
    public interface ISpatialSource
    {
        Task<bool> ExistsAsync(ElementKey key, CancellationToken token);
        Task VisitChildrenAsync(ElementKey key, Func<ElementKey,bool> visit, CancellationToken token);
        Task VisitInstancesAsync(ElementKey key, Func<SpatialInstance,Task> visit, CancellationToken token);
        Task VisitMeshAsync(SpatialInstance instance, Func<string,double[],Task> floats, Func<string,uint[],Task> indices, CancellationToken token);
    }

}
