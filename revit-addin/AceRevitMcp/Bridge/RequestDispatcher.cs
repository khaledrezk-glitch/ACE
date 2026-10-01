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
            public int Abandoned; // set when the caller timed out; never start it afterwards
        }

        private readonly ConcurrentQueue<Pending> _queue = new ConcurrentQueue<Pending>();
        private readonly CommandRegistry _registry;
        private ExternalEvent _event;

        /// <summary>How long a request may wait for Revit to pick it up; the run itself has the caller's full timeout.</summary>
        private static readonly TimeSpan PickupTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The command Revit is running now and since when (null when idle), for /health and busy messages.</summary>
        public static BusyInfo Busy { get; private set; }   // one reference, so the HTTP threads always read a whole value

        internal sealed record BusyInfo(string Command, DateTime Since);

        private static CancellationTokenSource _running = new CancellationTokenSource();

        /// <summary>Set when Claude cancels the running command: scripts check it with ctx.Cancelled, the clash run per element.</summary>
        public static CancellationToken RunningToken => _running.Token;

        /// <summary>Cancels the running command (where it checks) and drops every queued one (from any thread).</summary>
        public string CancelAll()
        {
            var busy = Busy;
            try { _running.Cancel(); } catch { }
            foreach (var p in _queue)
                if (Interlocked.Exchange(ref p.Abandoned, 1) == 0)
                    p.Completion.TrySetException(new OperationCanceledException("Cancelled by the user."));
            return busy?.Command;
        }

        public RequestDispatcher(CommandRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Must be called from a valid Revit API context (e.g. OnStartup).</summary>
        public void Initialize() => _event = ExternalEvent.Create(this);

        public async Task<JsonNode> EnqueueAsync(string command, JsonObject args, TimeSpan timeout)
        {
            var pending = new Pending
            {
                Command = command,
                Args = args ?? new JsonObject(),
                Completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously),
                Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _queue.Enqueue(pending);

            var request = _event.Raise();
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
                Interlocked.Exchange(ref _running, new CancellationTokenSource());   // not disposed: other threads may still read it
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
                finally { Busy = null; }
            }
        }

        public string GetName() => "ACE Revit MCP bridge";
    }
}
