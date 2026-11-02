using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
namespace AdvancedTools.BimBridge
{
    public sealed class SearchElementsRequest
    {
        public string TargetModelKey { get; set; }
        public DateTime? ModelVersionUtc { get; set; }
        public List<Guid> TargetModelPartIds { get; set; }
        public string LogicalOperator { get; set; } = "And";
        public List<SearchConditionRequest> Conditions { get; set; } = new List<SearchConditionRequest>();
        public int MaxResults { get; set; } = 10000;
        public int? PageSize { get; set; }
        public string Cursor { get; set; }
    }

    public sealed class SearchConditionRequest
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Operator { get; set; }
        public JToken Value { get; set; }
    }

}
