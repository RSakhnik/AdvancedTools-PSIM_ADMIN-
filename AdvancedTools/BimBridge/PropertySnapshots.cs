using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace AdvancedTools.BimBridge
{
    public sealed class PropertyReadSnapshot
    {
        public bool Exists { get; set; }
        public List<object> PropertySets { get; } = new List<object>();
        public int TotalPropertyCount { get; set; }
        public int SelectedPropertyCount { get; set; }
    }

    internal static class SnapshotPropertyReader
    {
        public static PropertyReadSnapshot ReadProperties(IModelStorage storage, ElementKey key, DateTime version, ISet<string> names, CancellationToken token = default(CancellationToken))
        {
            var id = new ModelElementId(key.ModelPartId, key.ElementId);
            var result = new PropertyReadSnapshot { Exists = storage.LoadElement(id, version) != null };
            if (!result.Exists) return result;
            foreach (var set in storage.LoadElementProperties(id, version) ?? Enumerable.Empty<IPropertySet>())
            {
                token.ThrowIfCancellationRequested();
                var properties = new List<object>();
                foreach (var property in set.Properties)
                {
                    token.ThrowIfCancellationRequested();
                    result.TotalPropertyCount++;
                    if (names != null && names.Count > 0 && !names.Contains(property.Name)) continue;
                    result.SelectedPropertyCount++;
                    properties.Add(new { property.Name, property.Unit, Value = PlainValue(property.Value) });
                }
                result.PropertySets.Add(new { Category = set.Name, Type = PlainValue(set.Type), Properties = properties });
            }
            return result;
        }
        private static object PlainValue(object value)
        {
            if (value == null || value is string || value is bool || value is byte || value is short || value is int ||
                value is long || value is float || value is double || value is decimal || value is DateTime || value is Guid || value.GetType().IsEnum)
                return value;
            return value.ToString();
        }
    }
}
