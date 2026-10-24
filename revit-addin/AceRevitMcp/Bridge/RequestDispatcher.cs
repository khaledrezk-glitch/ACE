using System;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AceRevitMcp.Commands;
using AceRevitMcp.Util;
using Autodesk.Revit.UI;

namespace AceRevitMcp.Bridge
{
    /// <summary>
    /// The Revit API may only be used on Revit's main thread. HTTP requests are queued here and
    /// executed inside an ExternalEvent, which Revit raises when it is idle.
    /// </summary>
    internal sealed class RequestDispatcher : IExternalEventHandler
    {
        private sealed class Pending
        {
            public string Command;
            public JsonObject Args;
            public TaskCompletionSource<JsonNode> Completion;
            public TaskCompletionSource<bool> Started;
            public int Abandoned; // set when the caller timed out or cancelled; never start it afterwards
            public string Id;     // the caller's request id, so a cancel reaches exactly this request
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
        }

        private readonly ConcurrentDictionary<string, Pending> _byId = new ConcurrentDictionary<string, Pending>();
        private static Pending _current;

        private readonly ConcurrentQueue<Pending> _queue = new ConcurrentQueue<Pending>();
        private readonly CommandRegistry _registry;
        private ExternalEvent _event;

        /// <summary>How long a request may wait for Revit to pick it up; the run itself has the caller's full timeout.</summary>
        private static readonly TimeSpan PickupTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The command Revit is running now and since when (null when idle), for /health and busy messages.</summary>
        public static BusyInfo Busy { get; private set; }   // one reference, so the HTTP threads always read a whole value

        internal sealed record BusyInfo(string Command, DateTime Since);

        /// <summary>
        /// The cancel token of the request Revit is running now (scripts check it with ctx.Cancelled, the clash run per
        /// element). Outside a bridge request (a ribbon button) it is never cancelled.
        /// </summary>
        public static CancellationToken RunningToken => _current?.Cancel.Token ?? CancellationToken.None;

        /// <summary>
        /// Cancels one request (from any thread): not started yet = dropped; running = its token is cancelled (it stops
        /// where it checks; work already done is rolled back). Other requests, queued or later, are not touched.
        /// </summary>
        public string Cancel(string id)
        {
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out var p)) return null;
            try { p.Cancel.Cancel(); } catch { }
            if (Interlocked.Exchange(ref p.Abandoned, 1) == 0)
            {
                p.Completion.TrySetException(new OperationCanceledException("Cancelled by the user before Revit started it. Nothing was changed."));
                p.Started.TrySetResult(false);
            }
            return p.Command;
        }

        public RequestDispatcher(CommandRegistry registry)
        {
            _registry = registry;
        }

        private bool _ownEvent;

        /// <summary>
        /// Must be called from a valid Revit API context (e.g. OnStartup). Under the ACE loader the loader's one
        /// ExternalEvent is used (it forwards to this handler); loaded directly, ACE creates its own.
        /// </summary>
        public void Initialize()
        {
            var shared = PayloadEntry.Get<ExternalEvent>("externalEvent");
            var target = PayloadEntry.Get<Action<IExternalEventHandler>>("setEventHandler");
            if (shared != null && target != null) { target(this); _event = shared; return; }
            _event = ExternalEvent.Create(this);
            _ownEvent = true;
        }

        /// <summary>
        /// Payload Stop: no more runs. Requests that have not started get a clear error (nothing ran), the loader's event
        /// no longer reaches this handler, and an event of our own is disposed.
        /// </summary>
        public void Shutdown()
        {
            try { PayloadEntry.Get<Action<IExternalEventHandler>>("setEventHandler")?.Invoke(null); } catch { }
            var ev = _event;
            _event = null;
            while (_queue.TryDequeue(out var p))
            {
                if (Interlocked.Exchange(ref p.Abandoned, 1) != 0) continue;
                p.Completion.TrySetException(new CommandException(Reloading));
                p.Started.TrySetResult(false);
            }
            if (_ownEvent) try { (ev as IDisposable)?.Dispose(); } catch { }
            Busy = null;
            _current = null;
        }

        private const string Reloading = "ACE is stopping or loading a new build, so this request was not run (nothing changed). Send it again in a few seconds.";

        public async Task<JsonNode> EnqueueAsync(string command, JsonObject args, TimeSpan timeout, string id = null)
        {
            var pending = new Pending
            {
                Id = id,
                Command = command,
                Args = args ?? new JsonObject(),
                Completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously),
                Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            if (!string.IsNullOrEmpty(id)) _byId[id] = pending;
            _queue.Enqueue(pending);
            try { return await Wait(pending, command, timeout).ConfigureAwait(false); }
            finally { if (!string.IsNullOrEmpty(id)) _byId.TryRemove(id, out _); }
        }

        private async Task<JsonNode> Wait(Pending pending, string command, TimeSpan timeout)
        {
            var ev = _event;
            if (ev == null)
            {
                if (Interlocked.Exchange(ref pending.Abandoned, 1) == 0) throw new CommandException(Reloading);
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            var request = ev.Raise();
            if (request != ExternalEventRequest.Accepted && request != ExternalEventRequest.Pending)
                Log.Warn($"ExternalEvent.Raise returned {request}");

            // Two waits: for Revit to pick the request up (short: a dialog or a running command blocks it), then for the run.
            var started = DateTime.Now;
            var pickup = timeout < PickupTimeout ? timeout : PickupTimeout;
            if (!await Within(pending.Started.Task, pickup).ConfigureAwait(false)
                && Interlocked.Exchange(ref pending.Abandoned, 1) == 0)
            {
                var busy = Busy;
                throw new TimeoutException(busy != null
                    ? $"Revit is still running '{busy.Command}' (since {busy.Since:HH:mm:ss}), so '{command}' could not start. It was cancelled and will NOT run later. Wait for the running command to finish, then try again."
                    : $"Revit did not start '{command}' within {pickup.TotalSeconds:0}s. Revit is busy: a dialog is open, a command is active (press Esc), or a model is loading. The request was cancelled and will NOT run later.");
            }
            if (!await Within(pending.Completion.Task, timeout - (DateTime.Now - started)).ConfigureAwait(false))
                throw new TimeoutException(
                    $"'{command}' is still running in Revit after {timeout.TotalSeconds:0}s. It will finish there; do NOT send it again. " +
                    "Check the result later (revit_status shows when Revit is free).");
            return await pending.Completion.Task.ConfigureAwait(false);
        }

        private static async Task<bool> Within(Task task, TimeSpan time)
        {
            if (time <= TimeSpan.Zero) return task.IsCompleted;
            using var timer = new CancellationTokenSource();
            var finished = await Task.WhenAny(task, Task.Delay(time, timer.Token)).ConfigureAwait(false);
            timer.Cancel();   // a finished call releases its timer at once instead of after the full timeout
            return finished == task;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var pending))
            {
                // Claim the request; if the caller already gave up, skip it so nothing runs unexpectedly late.
                if (Interlocked.Exchange(ref pending.Abandoned, 1) != 0)
                {
                    Log.Warn($"Skipped '{pending.Command}' because the caller already timed out.");
                    continue;
                }

                var started = DateTime.Now;
                Busy = new BusyInfo(pending.Command, started);
                _current = pending;
                pending.Started.TrySetResult(true);
                try
                {
                    var result = _registry.Execute(pending.Command, app, pending.Args);
                    var ms = (long)(DateTime.Now - started).TotalMilliseconds;
                    Companion.ActivityHub.RecordCommand(pending.Command, pending.Args, result, null, ms);
                    pending.Completion.TrySetResult(result);
                    Log.Info($"{pending.Command} ok ({ms} ms)");
                }
                catch (Exception ex)
                {
                    Log.Error($"{pending.Command} failed: {ex}");
                    Companion.ActivityHub.RecordCommand(pending.Command, pending.Args, null, ex, (long)(DateTime.Now - started).TotalMilliseconds);
                    pending.Completion.TrySetException(ex);
                }
                finally { Busy = null; _current = null; }
            }
        }

        public string GetName() => "ACE Revit MCP bridge";
    }
}
