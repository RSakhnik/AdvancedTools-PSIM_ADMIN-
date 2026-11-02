using System;
using System.Collections.Generic;
using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.Search.SDK.Serialization;
using Newtonsoft.Json.Linq;
using Pilot.BimBridge.Contracts;
namespace AdvancedTools.BimBridge
{
    internal static class SearchFilters
    {
        internal static SearchSetState Build(SearchElementsRequest request)
        {
            if (!Enum.TryParse(request.LogicalOperator, true, out SearchSetLogicalOperator logical))
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "LogicalOperator must be And or Or.");
            var root = new SearchSetGroupState
            {
                ItemType = SearchSetItemType.Group,
                GroupingOperator = logical,
                Items = new List<SearchSetStateItem>()
            };
            foreach (var condition in request.Conditions)
            {
                if (string.IsNullOrWhiteSpace(condition.Name))
                    throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "Search condition Name is required.");
                if (!Enum.TryParse(condition.DataType, true, out PropertyDataType dataType))
                    throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, $"Unknown DataType '{condition.DataType}'.");
                if (!Enum.TryParse(condition.Operator, true, out SearchSetEqualityOperator equality))
                    throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, $"Unknown Operator '{condition.Operator}'.");
                root.Items.Add(new SearchSetExpressionState
                {
                    ItemType = SearchSetItemType.Expression,
                    Category = condition.Category,
                    Field = condition.Name,
                    DataType = dataType,
                    EqualityOperator = equality,
                    Value = ConvertValue(condition.Value, dataType, equality)
                });
            }
            return new SearchSetState { RootGroup = root };
        }

        private static object ConvertValue(JToken token, PropertyDataType type, SearchSetEqualityOperator equality)
        {
            if (equality == SearchSetEqualityOperator.Defined || equality == SearchSetEqualityOperator.NotDefined)
                return null;
            if (token == null || token.Type == JTokenType.Null)
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "Search condition Value is required.");
            try
            {
                switch (type)
                {
                    case PropertyDataType.Boolean: return token.Value<bool>();
                    case PropertyDataType.Long: return token.Value<long>();
                    case PropertyDataType.Double: return token.Value<double>();
                    case PropertyDataType.DateTime: return token.Value<DateTime>().ToUniversalTime();
                    case PropertyDataType.Guid: return token.Value<Guid>();
                    default: return token.Value<string>();
                }
            }
            catch (Exception exception)
            {
                throw new BimBridgeException(BimBridgeErrorCodes.InvalidRequest, "Search condition Value has an invalid type.", false, exception);
            }
        }
    }
}
