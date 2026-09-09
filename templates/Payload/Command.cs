using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using C3dMCP.Sdk;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace C3dPayload
{
    /// <summary>The payload's command dispatcher. The host calls Run on AutoCAD's main thread with
    /// the document locked. Report through <paramref name="ctx"/> only.
    ///
    /// PING       — synchronous: a note, a diagnostic, done when Run returns.
    /// DEFERDEMO  — an async tail: draws N lines through the command queue (SendStringToExecute),
    ///              calls ctx.Defer() before returning and ctx.Complete() from the last CommandEnded.
    /// DEFERNOEND — same tail but never calls Complete, so the host's idle ping must infer the end.
    /// THROW      — throws, so the run fails by host.</summary>
    public sealed class Command : IC3dCommand
    {
        public void Run(string command, IRunContext ctx)
        {
            switch ((command ?? string.Empty).ToUpperInvariant())
            {
                case "PING": Ping(ctx); break;
                case "DEFERDEMO": StartTail(ctx, complete: true); break;
                case "DEFERNOEND": StartTail(ctx, complete: false); break;
                case "THROW": throw new InvalidOperationException("THROW command: deliberate failure");
                default: throw new ArgumentException("Unknown command: " + command);
            }
        }

        private static void Ping(IRunContext ctx)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            ctx.Log("PING from " + typeof(Command).Assembly.GetName().Name + " on thread " + Thread.CurrentThread.ManagedThreadId);
            ctx.Note("drawing", new Dictionary<string, object> { ["name"] = doc?.Name, ["args"] = ctx.Args.Count });
            ctx.Diagnostic("document-present", doc != null, new Dictionary<string, object> { ["name"] = doc?.Name });
            ctx.Complete(RunStatus.Completed, new Dictionary<string, object> { ["pong"] = true });
        }

        // ---- the async tail --------------------------------------------------------------------

        private sealed class Tail
        {
            public IRunContext Ctx;
            public Document Doc;
            public int Index, Count;
            public bool Complete;
            public long Ticket;
        }

        private static Tail _tail;

        private static void StartTail(IRunContext ctx, bool complete)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) { ctx.Complete(RunStatus.Failed, new Dictionary<string, object> { ["error"] = "no document" }); return; }
            int count = 5;
            string n; if (ctx.Args.TryGetValue("count", out n)) int.TryParse(n, out count);
            var tail = new Tail { Ctx = ctx, Doc = doc, Count = Math.Max(1, count), Complete = complete };
            _tail = tail;
            doc.CommandEnded += OnCommandEnded;
            doc.CommandCancelled += OnCommandEnded;
            doc.CommandFailed += OnCommandEnded;
            ctx.Log("tail: queueing " + tail.Count + " LINE commands through SendStringToExecute");
            ctx.Defer(quietSeconds: 2);
            SendNext(tail);   // the first command lands after Run returns
        }

        private static void SendNext(Tail tail)
        {
            if (tail.Index >= tail.Count)
            {
                Finish(tail);
                return;
            }
            int i = tail.Index;
            double y = i * 10.0;
            string cmd = "_.LINE 0," + y + " 100," + y + " \n";
            tail.Ctx.Log("tail: sending LINE " + (i + 1) + "/" + tail.Count);
            tail.Doc.SendStringToExecute(cmd, true, false, false);
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            var tail = _tail;
            if (tail == null) return;
            try
            {
                if (!string.Equals(e.GlobalCommandName, "LINE", StringComparison.OrdinalIgnoreCase)) return;
                tail.Ctx.Feedback("create", "Line", "step-" + (tail.Index + 1), "tail line " + (tail.Index + 1));
                tail.Index++;
                SendNext(tail);
            }
            catch (Exception ex)
            {
                tail.Ctx.Log("tail FATAL: " + ex.Message, "error");
                Detach(tail);
                tail.Ctx.Complete(RunStatus.Failed, new Dictionary<string, object> { ["error"] = ex.Message });
                _tail = null;
            }
        }

        private static void Finish(Tail tail)
        {
            Detach(tail);
            _tail = null;
            tail.Ctx.Diagnostic("tail-lines-drawn", tail.Index == tail.Count, new Dictionary<string, object> { ["expected"] = tail.Count, ["actual"] = tail.Index });
            if (tail.Complete)
            {
                tail.Ctx.Log("tail: finished, calling Complete");
                tail.Ctx.Complete(RunStatus.Completed, new Dictionary<string, object> { ["lines"] = tail.Index });
            }
            else
            {
                tail.Ctx.Log("tail: finished WITHOUT Complete (idle ping must infer)");
            }
        }

        private static void Detach(Tail tail)
        {
            try
            {
                tail.Doc.CommandEnded -= OnCommandEnded;
                tail.Doc.CommandCancelled -= OnCommandEnded;
                tail.Doc.CommandFailed -= OnCommandEnded;
            }
            catch { }
        }
    }
}
