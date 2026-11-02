using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
namespace AdvancedTools.BimBridge
{
    internal sealed class SpatialSdkCursor<T> : IDisposable
    {
        private IEnumerator<T> _iterator;
        public SpatialSdkCursor(IEnumerable<T> source) { _iterator=(source ?? Enumerable.Empty<T>()).GetEnumerator(); }
        public T[] Read(int size, CancellationToken token)
        {
            var list = new List<T>(size);
            while (list.Count < size) { token.ThrowIfCancellationRequested(); if (!_iterator.MoveNext()) break; list.Add(_iterator.Current); }
            return list.ToArray();
        }
        public void Dispose() { var iterator=Interlocked.Exchange(ref _iterator,null); iterator?.Dispose(); }
    }

}
