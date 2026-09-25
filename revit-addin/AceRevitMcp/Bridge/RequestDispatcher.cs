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
            public int Abandoned; // set when the caller timed out; never start it afterwards
        }

        private readonly ConcurrentQueue<Pending> _queue = new ConcurrentQueue<Pending>();
        private readonly CommandRegistry _registry;
        private ExternalEvent _event;

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
            };
            _queue.Enqueue(pending);

            var request = _event.Raise();
            if (request != ExternalEventRequest.Accepted && request != ExternalEventRequest.Pending)
                Log.Warn($"ExternalEvent.Raise returned {request}");

            var finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != pending.Completion.Task)
            {
                if (Interlocked.Exchange(ref pending.Abandoned, 1) == 0)
                    throw new TimeoutException(
                        $"Revit did not run '{command}' within {timeout.TotalSeconds:0}s. Revit is probably busy: " +
                        "a dialog is open, a command is active (press Esc), or it is still loading a model. " +
                        "The request was cancelled and will NOT run later.");
            }
            return await pending.Completion.Task.ConfigureAwait(false);
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
                try
                {
                    var result = _registry.Execute(pending.Command, app, pending.Args);
                    pending.Completion.TrySetResult(result);
                    Log.Info($"{pending.Command} ok ({(DateTime.Now - started).TotalMilliseconds:0} ms)");
                }
                catch (Exception ex)
                {
                    Log.Error($"{pending.Command} failed: {ex}");
                    pending.Completion.TrySetException(ex);
                }
            }
        }

        public string GetName() => "ACE Revit MCP bridge";
    }
}
