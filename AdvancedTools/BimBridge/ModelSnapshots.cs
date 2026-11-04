using Ascon.Pilot.Bim.SDK;
using Ascon.Pilot.Bim.SDK.ModelStorage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    // SDK values are inspected on their owning Dispatcher. Callers copy only plain data out.
    public sealed class ModelSdkAdapter
    {
        private IModelStorage _storage;
        private readonly Func<Func<object>, Task<object>> _dispatch;
        private readonly Func<Func<object>, CancellationToken, Task<object>> _dispatchWithToken;
        private readonly Action<IDisposable> _releaseLate;
        public ModelSdkAdapter(IModelStorage storage, Func<Func<object>, Task<object>> dispatch,
            Func<Func<object>, CancellationToken, Task<object>> dispatchWithToken = null, Action<IDisposable> releaseLate = null)
        {
            _storage = storage;
            _dispatch = dispatch ?? (f => Task.FromResult(f()));
            _dispatchWithToken = dispatchWithToken;
            _releaseLate = releaseLate;
        }
        public async Task<T> ReadAsync<T>(Func<IModelStorage, T> read, CancellationToken token, Action<long> dispatchWait = null)
        {
            token.ThrowIfCancellationRequested();
            var queuedAt = dispatchWait == null ? 0 : Stopwatch.GetTimestamp();
            Func<object> action = () =>
            {
                dispatchWait?.Invoke(Stopwatch.GetTimestamp() - queuedAt);
                token.ThrowIfCancellationRequested();
                var storage = _storage ?? throw new ObjectDisposedException(nameof(ModelSdkAdapter));
                return (object)read(storage);
            };
            var value = await (_dispatchWithToken == null ? _dispatch(action) : _dispatchWithToken(action, token)).ConfigureAwait(false);
            if (token.IsCancellationRequested && value is IDisposable disposable) _releaseLate?.Invoke(disposable);
            token.ThrowIfCancellationRequested();
            return (T)value;
        }
        public void Detach() { Interlocked.Exchange(ref _storage, null); }
        public Task ReleaseOwnedAsync(IDisposable resource)
        {
            if (resource == null) return Task.CompletedTask;
            if (_releaseLate == null) return _dispatch(() => { resource.Dispose(); return null; });
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new OwnedRelease(resource, completion);
            // QueueCleanup retains the owner until the previous native call exits.
            // Never submit disposal as a new cancellable read behind a timed-out call.
            _releaseLate(release);
            return completion.Task;
        }
        private sealed class OwnedRelease : IDisposable
        {
            private IDisposable _resource;
            private readonly TaskCompletionSource<bool> _completion;
            private int _attempted;
            public OwnedRelease(IDisposable resource, TaskCompletionSource<bool> completion) { _resource=resource;_completion=completion; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _attempted,1)!=0) return;
                try { _resource.Dispose(); _resource=null; _completion.TrySetResult(true); }
                catch (Exception error) { _completion.TrySetException(error); throw; }
            }
        }
    }

    public sealed class ElementReadError
    {
        public Guid ModelPartId { get; set; }
        public Guid ElementId { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    public static class SnapshotElementReader
    {
        public static void AssertSearchElement(IModelStorage storage, ElementKey key, DateTime version)
        {
            if (storage.LoadElement(new ModelElementId(key.ModelPartId, key.ElementId), version) == null)
                throw new BimBridgeException("SEARCH_INDEX_MISMATCH",
                    $"Search index element {key.ModelPartId:D}/{key.ElementId:D} is absent from snapshot {version.ToUniversalTime():O}.", true);
        }
        public static PropertyReadSnapshot ReadProperties(IModelStorage storage, ElementKey key, DateTime version, ISet<string> names, CancellationToken token = default(CancellationToken))
            => SnapshotPropertyReader.ReadProperties(storage,key,version,names,token);
        public static ElementRelationDescriptor ReadRelations(IModelStorage storage, ElementKey key, DateTime version,
            bool includeParent, bool includeChildren, int remaining, IList<string> warnings, CancellationToken token = default(CancellationToken))
        {
            var id = new ModelElementId(key.ModelPartId, key.ElementId);
            var element = storage.LoadElement(id, version);
            if (element == null) throw new BimBridgeException("ELEMENT_NOT_FOUND", "Element is absent from the selected snapshot.");
            var relation = new ElementRelationDescriptor { Element = GetElementRelationsOperationHandler.CreateDescriptor(element) };
            if (includeParent && element.ParentId != null && element.ParentId.ElementId != Guid.Empty && element.ParentId.ModelPartId != Guid.Empty)
            {
                relation.Parent = GetElementRelationsOperationHandler.CreateDescriptor(storage.LoadElement(element.ParentId, version));
                if (relation.Parent == null) warnings.Add($"{key.ModelPartId:D}/{key.ElementId:D}: parent element was not found.");
            }
            if (includeChildren)
                foreach (var child in storage.LoadChildElements(id, version) ?? Enumerable.Empty<IModelElement>())
                {
                    token.ThrowIfCancellationRequested();
                    if (relation.Children.Count >= remaining) { relation.ChildrenTruncated = true; break; }
                    var plain = GetElementRelationsOperationHandler.CreateDescriptor(child);
                    if (plain != null) relation.Children.Add(plain);
                }
            return relation;
        }
    }

    public static class SnapshotResultPublisher
    {
        public static async Task<PreparedResult> WriteAsync(ResultFileWriter writer, ModelAccess model, Guid taskId,
            Func<Newtonsoft.Json.JsonTextWriter, Task> write, CancellationToken token)
        {
            var result = await writer.WriteStreamingJsonAsync(taskId, write, token).ConfigureAwait(false);
            try
            {
                // Compression and hashing may outlive JSON production. Recheck before handing the result to upload.
                await model.AssertUnchangedAsync(token).ConfigureAwait(false);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
}
