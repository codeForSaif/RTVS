﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Common.Core;
using Microsoft.R.Host.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.R.Debugger {
    public sealed class DebugSession : IDisposable {
        // State machine that processes breakpoints hit. We need to issue several commands in response
        // in succession; this keeps track of where we were on the last interaction.
        private enum BreakpointHitProcessingState {
            None,
            BrowserSetDebug,
            Continue
        }

        private CancellationTokenSource _initialPromptCts = new CancellationTokenSource();
        private TaskCompletionSource<object> _stepTcs;
        private BreakpointHitProcessingState _bpHitProcState;
        private DebugStackFrame _bpHitFrame;

        // Key is filename + line number; value is the count of breakpoints set for that line.
        private Dictionary<Tuple<string, int>, int> _breakpoints = new Dictionary<Tuple<string, int>, int>();

        public IRSession RSession { get; set; }

        public event EventHandler Browse;

        public DebugSession(IRSession session) {
            RSession = session;
            RSession.BeforeRequest += RSession_BeforeRequest;
            RSession.AfterRequest += RSession_AfterRequest;
        }

        public void Dispose() {
            RSession.BeforeRequest -= RSession_BeforeRequest;
            RSession.AfterRequest -= RSession_AfterRequest;
            RSession = null;
        }

        private void ThrowIfDisposed() {
            if (RSession == null) {
                throw new ObjectDisposedException(nameof(DebugSession));
            }
        }

        public async Task InitializeAsync() {
            ThrowIfDisposed();

            string helpers;
            using (var stream = typeof(DebugSession).Assembly.GetManifestResourceStream(typeof(DebugSession).Namespace + ".DebugHelpers.R"))
            using (var reader = new StreamReader(stream)) {
                helpers = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            helpers = helpers.Replace("\r", "");

            REvaluationResult res;
            using (var eval = await RSession.BeginEvaluationAsync().ConfigureAwait(false)) {
                res = await eval.EvaluateAsync(helpers, reentrant: false).ConfigureAwait(false);
            }

            if (res.ParseStatus != RParseStatus.OK) {
                throw new InvalidDataException("DebugHelpers.R failed to parse: " + res.ParseStatus);
            } else if (res.Error != null) {
                throw new InvalidDataException("DebugHelpers.R failed to eval: " + res.Error);
            }

            // Attach might happen when session is already at the Browse prompt, in which case we have
            // missed the corresponding BeginRequest event, but we want to raise Browse anyway. So
            // grab an interaction and check the prompt.
            RSession.BeginInteractionAsync().ContinueWith(async t => {
                using (var inter = await t.ConfigureAwait(false)) {
                    // If we got AfterRequest before we got here, then that has already taken care of
                    // the prompt; or if it's not a Browse prompt, will do so in a future event. Bail out.
                    _initialPromptCts.Token.ThrowIfCancellationRequested();
                    // Otherwise, treat it the same as if AfterRequest just happened.
                    ProcessBrowsePrompt(inter.Contexts);
                }
            }).DoNotWait();
        }

        public async Task ExecuteBrowserCommandAsync(string command) {
            ThrowIfDisposed();
            using (var inter = await RSession.BeginInteractionAsync(isVisible: true).ConfigureAwait(false)) {
                if (IsInBrowseMode(inter.Contexts)) {
                    await inter.RespondAsync(command + "\n").ConfigureAwait(false);
                }
            }
        }

        internal async Task<REvaluationResult> EvaluateRawAsync(string expression) {
            ThrowIfDisposed();
            using (var eval = await RSession.BeginEvaluationAsync().ConfigureAwait(false)) {
                var res = await eval.EvaluateAsync(expression, reentrant: false).ConfigureAwait(false);
                if (res.ParseStatus != RParseStatus.OK || res.Error != null || res.Result == null) {
                    Debug.Fail($"Internal debugger evaluation {expression} failed: {res}");
                    throw new REvaluationException(res);
                }
                return res;
            }
        }

        public async Task<DebugEvaluationResult> EvaluateAsync(DebugStackFrame stackFrame, string expression, string env = "NULL") {
            ThrowIfDisposed();
            var res = await EvaluateRawAsync($".rtvs.eval({expression.ToRStringLiteral()}, {env})").ConfigureAwait(false);
            return DebugEvaluationResult.Parse(stackFrame, expression, JObject.Parse(res.Result));
        }

        public Task StepIntoAsync() {
            return StepAsync("s");
        }

        public Task StepOverAsync() {
            return StepAsync("n");
        }

        public Task StepOutAsync() {
            return StepAsync("browserSetDebug()", "c");
        }

        private async Task StepAsync(params string[] commands) {
            Debug.Assert(commands.Length > 0);
            ThrowIfDisposed();

            _stepTcs = new TaskCompletionSource<object>();
            for (int i = 0; i < commands.Length - 1; ++i) {
                await ExecuteBrowserCommandAsync(commands[i]).ConfigureAwait(false);
            }

            ExecuteBrowserCommandAsync(commands.Last()).DoNotWait();
            await _stepTcs.Task.ConfigureAwait(false);
        }

        public bool CancelStep() {
            ThrowIfDisposed();

            if (_stepTcs == null) {
                return false;
            }

            _stepTcs.TrySetCanceled();
            _stepTcs = null;
            return true;
        }

        public async Task<IReadOnlyList<DebugStackFrame>> GetStackFramesAsync() {
            ThrowIfDisposed();

            REvaluationResult res;
            using (var eval = await RSession.BeginEvaluationAsync().ConfigureAwait(false)) {
                res = await eval.EvaluateAsync(".rtvs.traceback()", reentrant: false).ConfigureAwait(false);
            }

            if (res.ParseStatus != RParseStatus.OK || res.Error != null || res.Result == null) {
                throw new InvalidDataException(".rtvs.traceback() failed");
            }

            JArray jFrames;
            try {
                jFrames = JArray.Parse(res.Result);
            } catch (JsonException ex) {
                throw new InvalidDataException("Failed to parse JSON returned by .rtvs.traceback()", ex);
            }

            var stackFrames = new List<DebugStackFrame>();

            DebugStackFrame lastFrame = null;
            int i = 0;
            foreach (JObject jFrame in jFrames) {
                try {
                    string fileName = (string)jFrame["filename"];
                    int? lineNumber = (int?)(double?)jFrame["linenum"];
                    bool isGlobal = (bool)jFrame["is_global"];
                    var fallbackFrame = (_bpHitFrame != null && _bpHitFrame.Index == i) ? _bpHitFrame : null;
                    lastFrame = new DebugStackFrame(this, i, lastFrame, jFrame, fallbackFrame);
                    stackFrames.Add(lastFrame);
                } catch (JsonException ex) {
                    Debug.Fail(ex.ToString());
                }
                ++i;
            }

            return stackFrames;
        }

        public async Task<int> AddBreakpointAsync(string fileName, int lineNumber) {
            // Tracer expression must be in sync with DebugStackFrame._breakpointRegex
            var tracer = $"quote({{.rtvs.breakpoint({fileName.ToRStringLiteral()}, {lineNumber})}})";
            var res = await EvaluateAsync(null, $"setBreakpoint({fileName.ToRStringLiteral()}, {lineNumber}, tracer={tracer})")
                .ConfigureAwait(false);
            if (res is DebugErrorEvaluationResult) {
                throw new InvalidOperationException($"{res.Expression}: {res}");
            }

            var location = Tuple.Create(fileName, lineNumber);
            if (!_breakpoints.ContainsKey(location)) {
                _breakpoints.Add(location, 0);
            }

            return ++_breakpoints[location];
        }

        public async Task<int> RemoveBreakpointAsync(string fileName, int lineNumber) {
            var location = Tuple.Create(fileName, lineNumber);
            if (!_breakpoints.ContainsKey(location)) {
                return 0;
            }

            var res = await EvaluateAsync(null, $"setBreakpoint({fileName.ToRStringLiteral()}, {lineNumber}, clear=TRUE)").ConfigureAwait(false);
            if (res is DebugErrorEvaluationResult) {
                throw new InvalidOperationException($"{res.Expression}: {res}");
            }

            int count = --_breakpoints[location];
            Debug.Assert(count >= 0);
            if (count == 0) {
                _breakpoints.Remove(location);
            }

            return count;
        }

        private bool IsInBrowseMode(IReadOnlyList<IRContext> contexts) {
            return contexts.SkipWhile(context => context.CallFlag.HasFlag(RContextType.Restart))
                .FirstOrDefault()?.CallFlag.HasFlag(RContextType.Browser) == true;
        }

        private void InterruptBreakpointHitProcessing() {
            _bpHitFrame = null;
            if (_bpHitProcState != BreakpointHitProcessingState.None) {
                _bpHitProcState = BreakpointHitProcessingState.None;
                // If we were in the middle of processing a breakpoint hit, we need to make sure that
                // any pending stepping is canceled, since we can no longer handle it at this point.
                CancelStep();
            }
        }

        private void ProcessBrowsePrompt(IReadOnlyList<IRContext> contexts) {
            if (!IsInBrowseMode(contexts)) {
                InterruptBreakpointHitProcessing();
                return;
            }

            RSession.BeginInteractionAsync().ContinueWith(async t => {
                using (var inter = await t) {
                    if (inter.Contexts != contexts) {
                        // Someone else has already responded to this interaction.
                        InterruptBreakpointHitProcessing();
                        return;
                    }

                    switch (_bpHitProcState) {
                        case BreakpointHitProcessingState.None:
                            {
                                var lastFrame = (await GetStackFramesAsync().ConfigureAwait(false)).LastOrDefault();
                                if (lastFrame?.FrameKind == DebugStackFrameKind.TracebackAfterBreakpoint) {
                                    // If we're stopped at a breakpoint, step out of .doTrace, so that the next stepping command that
                                    // happens is applied at the actual location inside the function where the breakpoint was set.
                                    // Determine how many steps out we need to make by counting back to .doTrace. Also stash away the
                                    // .doTrace frame - we will need it to correctly report filename and line number after we unwind.
                                    int n = 0;
                                    _bpHitFrame = lastFrame;
                                    for (
                                        _bpHitFrame = lastFrame;
                                        _bpHitFrame != null && _bpHitFrame.FrameKind != DebugStackFrameKind.DoTrace;
                                        _bpHitFrame = _bpHitFrame.CallingFrame
                                    ) {
                                        ++n;
                                    }

                                    // Set the destination for the next "c", which we will issue on the following prompt.
                                    await inter.RespondAsync($"browserSetDebug({n})\n");
                                    _bpHitProcState = BreakpointHitProcessingState.BrowserSetDebug;
                                    return;
                                } else {
                                    _bpHitFrame = null;
                                }
                                break;
                            }

                        case BreakpointHitProcessingState.BrowserSetDebug:
                            // We have issued a browserSetDebug() on the previous interaction prompt to set destination
                            // for the "c" command. Issue that command now, and move to the next step.
                            await inter.RespondAsync("c\n");
                            _bpHitProcState = BreakpointHitProcessingState.Continue;
                            return;

                        case BreakpointHitProcessingState.Continue:
                            // We have issued a "c" command on the previous interaction prompt to unwind the stack back
                            // to the function in which the breakpoint was set. We are in the proper context now, and
                            // can report step completion and raise Browse below.
                            _bpHitProcState = BreakpointHitProcessingState.None;
                            break;
                    }

                    if (_stepTcs != null) {
                        var stepTcs = _stepTcs;
                        _stepTcs = null;
                        stepTcs.TrySetResult(null);
                    }

                    Browse?.Invoke(this, EventArgs.Empty);
                }
            }).DoNotWait();
        }

        private void RSession_BeforeRequest(object sender, RRequestEventArgs e) {
            _initialPromptCts.Cancel();
            ProcessBrowsePrompt(e.Contexts);
        }

        private void RSession_AfterRequest(object sender, RRequestEventArgs e) {
        }
    }

    public class REvaluationException : Exception {
        public REvaluationResult Result { get; }

        public REvaluationException(REvaluationResult result) {
            Result = result;
        }
    }
}
