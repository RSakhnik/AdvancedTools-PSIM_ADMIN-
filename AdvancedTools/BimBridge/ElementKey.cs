using System;
using Ascon.Pilot.Bim.SDK;
namespace AdvancedTools.BimBridge
{
    public sealed class ElementKey
    {
        public Guid ModelPartId { get; set; }
        public Guid ElementId { get; set; }
    }

}

namespace AdvancedTools.BimBridge
{
    internal sealed class ModelElementId : IModelElementId
    {
        public ModelElementId(Guid modelPartId, Guid elementId)
        {
            ModelPartId = modelPartId;
            ElementId = elementId;
        }
        public Guid ElementId { get; }
        public Guid ModelPartId { get; }
    }

}
