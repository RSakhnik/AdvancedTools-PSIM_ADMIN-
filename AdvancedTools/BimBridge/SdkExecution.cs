using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AdvancedTools.BimBridge
{
    public sealed class SdkCallGateway
    {
        private sealed class Call
        {
            public long Id; public string Stage; public volatile bool Started, Recovery, CleanupFailed, Cleanup;
            public int Phase, OwnsLane;
            public DateTime QueuedAt, StartedAt;
            public object RetainedOwner;
            public DispatcherOperation Pending;
            public readonly CancellationTokenSource QueueCancellation = new CancellationTokenSource();
            public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly Dispatcher _dispatcher;
        private readonly BimBridgeSettings _settings;
        private readonly IBimBridgeLog _log;
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<long, Call> _calls = new ConcurrentDictionary<long, Call>();
        private long _sequence,_dispatcherInvocations;
        public long SdkReadCalls=>Interlocked.Read(ref _sequence);
        public long DispatcherInvocations=>Interlocked.Read(ref _dispatcherInvocations);
        public SdkCallGateway(Dispatcher dispatcher, BimBridgeSettings settings, IBimBridgeLog log = null)
        { _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)); _settings = settings; _log = log; }
        public bool HasRecovery => _calls.Values.Any(c => c.Recovery);
        public bool HasProlongedRecovery => _calls.Values.Any(c => c.Recovery && DateTime.UtcNow - c.QueuedAt > TimeSpan.FromSeconds(30));
        public int PendingCount => _calls.Count;
        public string RecoveryReason => _calls.Values.Any(c => c.Recovery && c.Cleanup) ? "SDK_CLEANUP_PENDING" : "SDK_RECOVERY_PENDING";
        public string Status => string.Join("; ", _calls.Values.Where(c => c.Recovery).Select(c => c.Stage + (c.CleanupFailed ? ": освобождение не завершено, нужен перезапуск" : c.Cleanup ? ": не завершено освобождение ресурсов" : ": SDK не завершил вызов")));
        public Task RecoveryTask => Task.WhenAll(_calls.Values.Where(c => c.Recovery).Select(c => c.Done.Task));

        public Task<T> RunAsync<T>(Func<T> action, string stage, CancellationToken token = default(CancellationToken), Func<T,Task> releaseLate = null)
            => RunAsync(() => Task.FromResult(action()), stage, Math.Max(1, _settings.OperationTimeoutSeconds), token, releaseLate);

        public async Task<T> RunAsync<T>(Func<Task<T>> action, string stage, int timeoutSeconds, CancellationToken token, Func<T,Task> releaseLate = null)
        {
            token.ThrowIfCancellationRequested();
            var call = new Call { Id = Interlocked.Increment(ref _sequence), Stage = stage, QueuedAt = DateTime.UtcNow, RetainedOwner = action };
            _calls[call.Id] = call;
            var watch = Stopwatch.StartNew();
            if (stage != "SDK snapshot/read") _log?.Info($"SDK queued: call={call.Id}; stage={stage}; queuedUtc={call.QueuedAt:O}.");
            var execute = ExecuteAsync(action, call, token);
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var total = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(timeoutSeconds, _settings.OperationTimeoutSeconds))), timer.Token);
                var queue = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.SdkDispatcherQueueTimeoutSeconds)), timer.Token);
                while (!execute.IsCompleted)
                {
                    await Task.WhenAny(execute, total, call.Started ? Task.Delay(200, timer.Token) : queue).ConfigureAwait(false);
                    if (execute.IsCompleted) break;
                    if (!token.IsCancellationRequested && !total.IsCompleted && (call.Started || !queue.IsCompleted)) continue;
                    call.Recovery = true;
                    if (Interlocked.CompareExchange(ref call.Phase, 2, 0) == 0)
                    {
                        call.QueueCancellation.Cancel();
                        call.Pending?.Abort();
                    }
                    // The token is checked again inside the queued delegate. An unstarted call cannot execute after abandonment.
                    callAbandoned[call.Id] = true;
                    _ = ReleaseAfterCompletionAsync(execute, call, releaseLate);
                    _log?.Warn($"SDK deadline: call={call.Id}; stage={stage}; started={call.Started}; queuedUtc={call.QueuedAt:O}; startedUtc={call.StartedAt:O}; queueAborted={call.Phase == 2}; elapsedMs={watch.ElapsedMilliseconds}.");
                    token.ThrowIfCancellationRequested();
                    throw new BimBridgeException(call.Started ? (stage.StartsWith("Search initialization", StringComparison.Ordinal) || stage.Contains("factory") ? "SDK_INITIALIZATION_TIMEOUT" : "SDK_RECOVERY_PENDING") : "SDK_UI_BUSY",
                        stage + (call.Started ? ": SDK did not finish in time." : ": Pilot Dispatcher did not start the call."), true);
                }
                timer.Cancel();
                try
                {
                    var value = await execute.ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        call.Recovery = true;
                        _ = ReleaseAfterCompletionAsync(Task.FromResult(value), call, releaseLate);
                        token.ThrowIfCancellationRequested();
                    }
                    Finish(call);
                    if (stage != "SDK snapshot/read" || watch.ElapsedMilliseconds > 1000)
                        _log?.Info($"SDK finished: call={call.Id}; stage={stage}; queuedUtc={call.QueuedAt:O}; startedUtc={call.StartedAt:O}; elapsedMs={watch.ElapsedMilliseconds}.");
                    return value;
                }
                catch { if (!call.Recovery) Finish(call); throw; }
            }
        }
        private readonly ConcurrentDictionary<long,bool> callAbandoned = new ConcurrentDictionary<long,bool>();
        private async Task<T> ExecuteAsync<T>(Func<Task<T>> action, Call call, CancellationToken token)
        {
            await _serial.WaitAsync(call.QueueCancellation.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref call.OwnsLane, 1);
                if (callAbandoned.ContainsKey(call.Id)) throw new OperationCanceledException();
                var dispatch = _dispatcher.InvokeAsync(() =>
                {
                    Interlocked.Increment(ref _dispatcherInvocations);
                    token.ThrowIfCancellationRequested();
                    if (Interlocked.CompareExchange(ref call.Phase, 1, 0) != 0) throw new OperationCanceledException();
                    call.Started = true;
                    call.StartedAt = DateTime.UtcNow;
                    if (call.Stage != "SDK snapshot/read") _log?.Info($"SDK started: call={call.Id}; stage={call.Stage}; startedUtc={call.StartedAt:O}.");
                    return action();
                }, DispatcherPriority.Background);
                call.Pending = dispatch;
                if (Volatile.Read(ref call.Phase) == 2) dispatch.Abort();
                var sdkTask = await dispatch.Task.ConfigureAwait(false);
                return await sdkTask.ConfigureAwait(false);
        }
        private async Task ReleaseAfterCompletionAsync<T>(Task<T> execute, Call call, Func<T,Task> release)
        {
            try
            {
                T value;
                try { value = await execute.ConfigureAwait(false); }
                catch { Finish(call); return; }
                call.Cleanup = true;
                call.RetainedOwner = value;
                _ = WarnCleanupAsync(call);
                if (release != null) await release(value).ConfigureAwait(false);
                Finish(call);
            }
            catch (Exception error)
            {
                call.Recovery = true; call.CleanupFailed = true;
                _log?.Error($"SDK late cleanup failed: call={call.Id}; stage={call.Stage}.", error);
            }
        }
        private void Finish(Call call)
        {
            if (Interlocked.Exchange(ref call.OwnsLane, 0) == 1) _serial.Release();
            call.RetainedOwner = null;
            _calls.TryRemove(call.Id, out _); callAbandoned.TryRemove(call.Id, out _); call.Done.TrySetResult(true);
            call.QueueCancellation.Dispose();
        }
        public Task DisposeOnOwnerAsync(Action dispose) => _dispatcher.InvokeAsync(dispose, DispatcherPriority.Background).Task;
        public void QueueCleanup(Action dispose, Action released, string stage)
        {
            var call = new Call { Id = Interlocked.Increment(ref _sequence), Stage = stage, Recovery = true, Cleanup = true, RetainedOwner = dispose, QueuedAt = DateTime.UtcNow };
            _calls[call.Id] = call;
            _ = WarnCleanupAsync(call);
            _ = Task.Run(async () =>
            {
                await _serial.WaitAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref call.OwnsLane, 1);
                try
                {
                    await DisposeOnOwnerAsync(dispose).ConfigureAwait(false);
                    released?.Invoke(); Finish(call);
                }
                catch (Exception error) { call.CleanupFailed = true; _log?.Error("SDK cleanup failed: " + stage, error); }
            });
        }
        private async Task WarnCleanupAsync(Call call)
        {
            var seconds = Math.Max(1, Math.Min(_settings.SdkCleanupTimeoutSeconds, _settings.OperationTimeoutSeconds));
            if (await Task.WhenAny(call.Done.Task, Task.Delay(TimeSpan.FromSeconds(seconds))).ConfigureAwait(false) != call.Done.Task)
                _log?.Warn($"SDK_CLEANUP_PENDING: call={call.Id}; stage={call.Stage}; elapsedSeconds={seconds}. No new assignments until cleanup completes.");
        }
    }

    public sealed class PilotUiMonitor : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly uint _pilotThread;
        private readonly uint _processId = (uint)Process.GetCurrentProcess().Id;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _probe;
        private DateTime _probeAt, _blockedAt;
        private IntPtr _popup;
        private readonly IBimBridgeLog _log;
        public string WindowStatus { get; private set; } = "Окна ещё не проверены";
        public bool Blocked { get; private set; }
        public string Reason { get; private set; } = "Pilot доступен";
        public bool Prolonged => Blocked && DateTime.UtcNow - _blockedAt > TimeSpan.FromSeconds(30);
        public PilotUiMonitor(Dispatcher dispatcher, IBimBridgeLog log = null)
        { _dispatcher = dispatcher; _log = log; _pilotThread = GetCurrentThreadId(); _ = Task.Run(MonitorAsync); }
        private async Task MonitorAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (_probe == null || _probe.IsCompleted)
                    { _probeAt = DateTime.UtcNow; _probe = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task; }
                    var modal = FindPopup();
                    // Pilot may keep visible shell windows disabled without an active modal dialog.
                    // An unpaired disabled window is diagnostic only; the dispatcher timeout still
                    // prevents SDK calls from running while the UI thread is actually stalled.
                    var dispatcherUnresponsive = DateTime.UtcNow - _probeAt > TimeSpan.FromSeconds(5);
                    var blocked = modal != IntPtr.Zero || dispatcherUnresponsive;
                    if (blocked && !Blocked) _blockedAt = DateTime.UtcNow;
                    if (blocked != Blocked) _log?.Info("Pilot UI state: blocked=" + blocked + "; " + WindowStatus + "; dispatcherProbeAgeSeconds=" + (DateTime.UtcNow - _probeAt).TotalSeconds.ToString("F1") + ".");
                    _popup = modal; Blocked = blocked;
                    Reason = modal != IntPtr.Zero ? "Открыто модальное окно Pilot" : dispatcherUnresponsive ? "Pilot занят: Dispatcher не отвечает" : "Pilot доступен";
                    await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch { if (!Blocked) _blockedAt = DateTime.UtcNow; Blocked = true; Reason = "Не удалось проверить UI Pilot"; await Task.Delay(2000).ConfigureAwait(false); }
            }
        }
        private IntPtr FindPopup()
        {
            IntPtr disabled = IntPtr.Zero, popup = IntPtr.Zero;
            var visible = 0; var disabledCount = 0; var owned = 0;
            EnumWindows((h,p) =>
            {
                uint pid; var thread = GetWindowThreadProcessId(h, out pid);
                if (pid != _processId || thread != _pilotThread) return true;
                if (IsWindowVisible(h)) visible++;
                if (!IsWindowEnabled(h)) { disabledCount++; if (IsWindowVisible(h)) disabled = h; }
                var owner = GetWindow(h, 4);
                if (owner != IntPtr.Zero)
                {
                    owned++;
                    if (!IsWindowEnabled(owner) && IsWindowEnabled(h)) popup = h;
                }
                return true;
            }, IntPtr.Zero);
            if (disabled != IntPtr.Zero)
            {
                var candidate = GetLastActivePopup(disabled);
                uint pid;
                if (candidate != disabled && IsWindow(candidate) && IsWindowEnabled(candidate) &&
                    GetWindowThreadProcessId(candidate, out pid) == _pilotThread && pid == _processId) popup = candidate;
            }
            WindowStatus = $"Окна Pilot: видимых {visible}, отключённых {disabledCount}, с владельцем {owned}, popup={(popup != IntPtr.Zero)}";
            return popup;
        }
        public bool ShowPopup()
        {
            var popup = _popup; uint pid;
            if (popup == IntPtr.Zero || !IsWindow(popup) || GetWindowThreadProcessId(popup, out pid) != _pilotThread || pid != _processId) return false;
            ShowWindow(popup, 9);
            Rect rect;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (GetWindowRect(popup, out rect) && GetMonitorInfo(MonitorFromWindow(popup, 2), ref info))
            {
                var work = info.Work;
                if (rect.Right <= work.Left || rect.Left >= work.Right || rect.Bottom <= work.Top || rect.Top >= work.Bottom)
                    SetWindowPos(popup, IntPtr.Zero, work.Left + 24, work.Top + 24, 0, 0, 0x0015);
            }
            return SetForegroundWindow(popup);
        }
        public void Dispose() { _stop.Cancel(); }
        private delegate bool WindowCallback(IntPtr h, IntPtr p);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet=CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr h, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    }
}
