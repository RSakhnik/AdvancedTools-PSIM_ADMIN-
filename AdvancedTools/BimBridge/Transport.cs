using Ascon.Pilot.SDK;
using Pilot.BimBridge.Contracts;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace AdvancedTools.BimBridge
{
    public interface IBimBridgeServerClient
    {
        Task<BimBridgeHealthDto> GetHealthAsync();
        Task<IReadOnlyList<WorkerStatusDto>> GetWorkersAsync();
        Task<RegisterWorkerResponseDto> RegisterWorkerAsync(RegisterWorkerRequestDto request);
        Task UnregisterWorkerAsync(Guid registrationId);
        Task<WaitForTaskResponseDto> WaitForTaskAsync(Guid registrationId, int timeoutSeconds);
        Task<StartTaskResponseDto> StartTaskAsync(Guid registrationId, Guid taskId, Guid assignmentId);
        Task<WorkerHeartbeatResponseDto> HeartbeatAsync(WorkerHeartbeatRequestDto request);
        Task<BeginResultUploadResponseDto> BeginUploadAsync(BeginResultUploadRequestDto request);
        Task<UploadResultChunkResponseDto> UploadChunkAsync(Guid uploadId, int index, byte[] data);
        Task<CompleteTaskResponseDto> CompleteTaskAsync(Guid registrationId, Guid taskId, Guid assignmentId, Guid uploadId);
        Task<FailTaskResponseDto> FailTaskAsync(FailTaskRequestDto request);
    }

    public sealed class PilotSessionCustomApiProvider
    {
        private static readonly string[] SessionGraphFields =
        {
            "_repository",
            "_repositories",
            "_repositoryContainers",
            "_dataProvider",
            "_provider",
            "_storage",
            "_remote",
            "_serverApi",
            "_client"
        };
        private readonly IObjectsRepository _objectsRepository;
        private readonly IBimBridgeLog _log;

        public PilotSessionCustomApiProvider(IObjectsRepository objectsRepository, IBimBridgeLog log)
        {
            _objectsRepository = objectsRepository ?? throw new ArgumentNullException(nameof(objectsRepository));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public BimBridgeServerClient CreateClient()
        {
            try
            {
                var transportAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(x => string.Equals(x.GetName().Name, "Ascon.Pilot.Transport", StringComparison.OrdinalIgnoreCase));
                if (transportAssembly == null)
                    throw Unsupported("Ascon.Pilot.Transport is not loaded in the current Pilot session.");

                var version = transportAssembly.GetName().Version;

                var pilotClient = FindPilotClient(_objectsRepository);
                var callback = new NoOpCallback();
                var workerApi = (IBimBridgeWorkerApi)GetCustomApi(
                    pilotClient, typeof(IBimBridgeWorkerApi), callback);
                var diagnosticsApi = (IBimBridgeDiagnosticsApi)GetCustomApi(
                    pilotClient, typeof(IBimBridgeDiagnosticsApi), callback);
                _log.Info(
                    $"Connected to BIM Bridge through the authenticated Pilot session " +
                    $"({pilotClient.GetType().FullName}, transport {version}).");
                return new BimBridgeServerClient(workerApi, diagnosticsApi);
            }
            catch (BimBridgeException)
            {
                throw;
            }
            catch (TargetInvocationException exception)
            {
                var cause = exception.InnerException ?? exception;
                throw Unsupported(
                    "Pilot session rejected BIM Bridge API creation: " + DescribeException(cause),
                    cause);
            }
            catch (Exception exception)
            {
                throw Unsupported(
                    "Unable to locate the current Pilot transport session: " + DescribeException(exception),
                    exception);
            }
        }

        private static object FindPilotClient(object root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var queue = new Queue<GraphNode>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var inspectedTypes = new List<string>();
            queue.Enqueue(new GraphNode(root, root.GetType().FullName, 0));

            while (queue.Count > 0 && visited.Count < 512)
            {
                var node = queue.Dequeue();
                if (node.Value == null || !visited.Add(node.Value))
                    continue;

                var typeName = node.Value.GetType().FullName;
                if (!inspectedTypes.Contains(typeName))
                    inspectedTypes.Add(typeName);

                if (HasCustomApiFactory(node.Value))
                    return node.Value;
                if (node.Depth >= 12)
                    continue;

                foreach (var fieldName in SessionGraphFields)
                {
                    object child;
                    try { child = GetField(node.Value, fieldName); }
                    catch { continue; }
                    EnqueueGraphValue(
                        queue,
                        child,
                        node.Path + " -> " + fieldName,
                        node.Depth + 1);
                }
            }

            throw new MissingMethodException(
                "HttpPilotClient.GetCustomApi was not found. Inspected runtime types: " +
                string.Join(" -> ", inspectedTypes.Take(24)));
        }

        private static void EnqueueGraphValue(
            Queue<GraphNode> queue,
            object value,
            string path,
            int depth)
        {
            if (value == null || value is string)
                return;

            var enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                queue.Enqueue(new GraphNode(value, path, depth));
                return;
            }

            var index = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                        queue.Enqueue(new GraphNode(item, path + "[" + index + "]", depth));
                    if (++index >= 64)
                        break;
                }
            }
            catch
            {
                // Some Pilot proxy collections can only be enumerated in their owning context.
            }
        }

        private static bool HasCustomApiFactory(object value)
        {
            return value != null && value.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(x => x.Name == "GetCustomApi" && x.IsGenericMethodDefinition);
        }

        private static object GetCustomApi(object pilotClient, Type apiType, IBimBridgeClientCallback callback)
        {
            var methods = pilotClient.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.Name == "GetCustomApi" && x.IsGenericMethodDefinition)
                .ToArray();

            var callbackOverload = methods.FirstOrDefault(x =>
                x.GetGenericArguments().Length == 2 &&
                x.GetParameters().Length == 1);
            if (callbackOverload != null)
            {
                return callbackOverload
                    .MakeGenericMethod(apiType, typeof(IBimBridgeClientCallback))
                    .Invoke(pilotClient, new object[] { callback });
            }

            var apiOnlyOverload = methods.FirstOrDefault(x =>
                x.GetGenericArguments().Length == 1 &&
                x.GetParameters().Length == 0);
            if (apiOnlyOverload != null)
                return apiOnlyOverload.MakeGenericMethod(apiType).Invoke(pilotClient, null);

            throw new MissingMethodException(
                $"{pilotClient.GetType().FullName}.GetCustomApi<TApi,TCallback> was not found.");
        }

        private static string DescribeException(Exception exception)
        {
            var descriptions = new List<string>();
            for (var current = exception; current != null && descriptions.Count < 5; current = current.InnerException)
                descriptions.Add($"{current.GetType().Name}: {current.Message}");
            return string.Join(" -> ", descriptions);
        }

        private static object GetField(object value, string fieldName)
        {
            if (value == null) return null;
            for (var type = value.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) return field.GetValue(value);
            }
            return null;
        }

        private static BimBridgeException Unsupported(string message, Exception inner = null)
        {
            return new BimBridgeException(BimBridgeErrorCodes.UnsupportedPilotVersion, message, false, inner);
        }

        private sealed class GraphNode
        {
            public GraphNode(object value, string path, int depth)
            {
                Value = value;
                Path = path;
                Depth = depth;
            }

            public object Value { get; }
            public string Path { get; }
            public int Depth { get; }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance =
                new ReferenceEqualityComparer();

            private ReferenceEqualityComparer() { }

            public new bool Equals(object left, object right) =>
                ReferenceEquals(left, right);

            public int GetHashCode(object value) =>
                RuntimeHelpers.GetHashCode(value);
        }

        private sealed class NoOpCallback : IBimBridgeClientCallback
        {
            public void OnNotification(BimBridgeNotificationDto notification) { }
        }
    }

    public sealed class BimBridgeServerClient : IBimBridgeServerClient
    {
        private readonly IBimBridgeWorkerApi _worker;
        private readonly IBimBridgeDiagnosticsApi _diagnostics;

        public BimBridgeServerClient(IBimBridgeWorkerApi worker, IBimBridgeDiagnosticsApi diagnostics)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        }

        public Task<BimBridgeHealthDto> GetHealthAsync() => _diagnostics.GetHealthAsync();

        public async Task<IReadOnlyList<WorkerStatusDto>> GetWorkersAsync()
        {
            return await _diagnostics.GetWorkersAsync().ConfigureAwait(false) ?? new List<WorkerStatusDto>();
        }

        public Task<RegisterWorkerResponseDto> RegisterWorkerAsync(RegisterWorkerRequestDto request) =>
            _worker.RegisterWorkerAsync(request);

        public Task UnregisterWorkerAsync(Guid registrationId) =>
            _worker.UnregisterWorkerAsync(new UnregisterWorkerRequestDto { RegistrationId = registrationId });

        public Task<WaitForTaskResponseDto> WaitForTaskAsync(Guid registrationId, int timeoutSeconds) =>
            _worker.WaitForTaskAsync(new WaitForTaskRequestDto
            {
                RegistrationId = registrationId,
                WaitTimeoutSeconds = timeoutSeconds
            });

        public Task<StartTaskResponseDto> StartTaskAsync(Guid registrationId, Guid taskId, Guid assignmentId) =>
            _worker.StartTaskAsync(new StartTaskRequestDto
            {
                RegistrationId = registrationId,
                TaskId = taskId,
                AssignmentId = assignmentId
            });

        public Task<WorkerHeartbeatResponseDto> HeartbeatAsync(WorkerHeartbeatRequestDto request) =>
            _worker.SendHeartbeatAsync(request);

        public Task<BeginResultUploadResponseDto> BeginUploadAsync(BeginResultUploadRequestDto request) =>
            _worker.BeginResultUploadAsync(request);

        public Task<UploadResultChunkResponseDto> UploadChunkAsync(Guid uploadId, int index, byte[] data) =>
            _worker.UploadResultChunkAsync(new UploadResultChunkRequestDto
            {
                UploadId = uploadId,
                ChunkIndex = index,
                Data = data
            });

        public Task<CompleteTaskResponseDto> CompleteTaskAsync(
            Guid registrationId, Guid taskId, Guid assignmentId, Guid uploadId) =>
            _worker.CompleteTaskAsync(new CompleteTaskRequestDto
            {
                RegistrationId = registrationId,
                TaskId = taskId,
                AssignmentId = assignmentId,
                UploadId = uploadId
            });

        public Task<FailTaskResponseDto> FailTaskAsync(FailTaskRequestDto request) => _worker.FailTaskAsync(request);
    }
}
