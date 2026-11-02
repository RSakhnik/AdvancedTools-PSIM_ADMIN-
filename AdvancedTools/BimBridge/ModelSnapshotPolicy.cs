using System;
using System.Collections.Generic;
using System.Linq;
namespace AdvancedTools.BimBridge
{
    public static class ModelSnapshotPolicy
    {
        public static DateTime Select(IEnumerable<DateTime> versions, DateTime? requested)
        {
            var available = (versions ?? Enumerable.Empty<DateTime>()).Where(x => x != DateTime.MinValue).ToArray();
            if (requested.HasValue)
            {
                var match = available.FirstOrDefault(x => x.ToUniversalTime() == requested.Value.ToUniversalTime());
                if (match == DateTime.MinValue)
                    throw new BimBridgeException("MODEL_VERSION_NOT_AVAILABLE", "The requested model snapshot is unavailable.", true);
                return match;
            }
            if (available.Length == 0)
                throw new BimBridgeException("MODEL_VERSION_NOT_AVAILABLE", "No model snapshot is available.", true);
            return available.OrderByDescending(x => x.ToUniversalTime()).First();
        }

        public static void AssertUnchanged(DateTime initialLatest, IEnumerable<DateTime> current)
        {
            if (Select(current, null).ToUniversalTime() != initialLatest.ToUniversalTime())
                throw new BimBridgeException("MODEL_CHANGED", "The model changed during the operation. Restart the export.", true);
        }

        public static void AssertSearchVersion(DateTime version, DateTime latest)
        {
            if (version.ToUniversalTime() != latest.ToUniversalTime())
                throw new BimBridgeException("SEARCH_VERSION_UNSUPPORTED", "Pilot search supports only the latest snapshot.");
        }
    }

}
