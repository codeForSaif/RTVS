﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.R.Host.Client;
using Microsoft.Common.Core;
using System.IO;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;

namespace Microsoft.R.Debugger {
    public sealed class DebugSession : IDisposable {
        private List<DebugStackFrame> _stackFrames = new List<DebugStackFrame>();
        private TaskCompletionSource<object> _stepTcs;

        public IRSession RSession { get; }

        public event EventHandler Paused;
        public event EventHandler Resumed;
        public event EventHandler BreakpointHit;

        public DebugSession(IRSession session) {
            RSession = session;
            RSession.BeforeRequest += RSession_BeforeRequest;
            RSession.AfterRequest += RSession_AfterRequest;
        }

        public void Dispose() {
            RSession.BeforeRequest -= RSession_BeforeRequest;
            RSession.AfterRequest -= RSession_AfterRequest;
        }

        public async Task Initialize() {
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
                throw new InvalidDataException("InjectHelpers failed to parse: " + res.ParseStatus);
            } else if (res.Error != null) {
                throw new InvalidDataException("InjectHelpers failed to eval: " + res.Error);
            }
        }

        public IReadOnlyList<DebugStackFrame> StackFrames {
            get {
                return _stackFrames;
            }
        }

        public async Task ExecuteBrowserCommandAsync(string command) {
            using (var inter = await RSession.BeginInteractionAsync(isVisible: true).ConfigureAwait(false)) {
                if (IsInBrowser(inter.Contexts)) {
                    await inter.RespondAsync(command + "\n").ConfigureAwait(false);
                }
            }
        }

        internal async Task<REvaluationResult> EvaluateRawAsync(string expression, bool throwOnError = true) {
            using (var eval = await RSession.BeginEvaluationAsync().ConfigureAwait(false)) {
                var res = await eval.EvaluateAsync(expression, reentrant: false).ConfigureAwait(false);
                if (throwOnError && (res.ParseStatus != RParseStatus.OK || res.Error != null || res.Result == null)) {
                    throw new REvaluationException(res);
                }
                return res;
            }
        }

        public async Task<DebugEvaluationResult> EvaluateAsync(DebugStackFrame stackFrame, string expression, string env = "NULL") {
            var quotedExpr = expression.Replace("\\", "\\\\").Replace("'", "\'");
            var res = await EvaluateRawAsync($".rtvs.eval('{quotedExpr}', ${env})", throwOnError: false).ConfigureAwait(false);

            if (res.ParseStatus != RParseStatus.OK) {
                return new DebugFailedEvaluationResult(expression, res.ParseStatus.ToString());
            } else if (res.Error != null) {
                return new DebugFailedEvaluationResult(expression, res.Error);
            } else if (res.Result == null) {
                return new DebugFailedEvaluationResult(expression, "No result");
            }

            return new DebugSuccessfulEvaluationResult(stackFrame, expression, JObject.Parse(res.Result));
        }

        public Task StepIntoAsync() {
            return Step("s");
        }

        public Task StepOverAsync() {
            return Step("n");
        }

        public Task StepOutAsync() {
            return Step("browserSetDebug()", "c");
        }

        private async Task Step(params string[] commands) {
            Debug.Assert(commands.Length > 0);
            _stepTcs = new TaskCompletionSource<object>();
            for (int i = 0; i < commands.Length - 1; ++i) {
                await ExecuteBrowserCommandAsync(commands[i]);
            }

            ExecuteBrowserCommandAsync(commands.Last()).DoNotWait();
            await _stepTcs.Task;
        }

        public void CancelStep() {
            if (_stepTcs == null) {
                throw new InvalidOperationException("No step to end.");
            }

            _stepTcs.TrySetCanceled();
            _stepTcs = null;
        }

        private async Task FetchStackFrames(IRSessionEvaluation eval) {
            _stackFrames.Clear();

            var res = await eval.EvaluateAsync(".rtvs.traceback()", reentrant: false).ConfigureAwait(false);
            if (res.ParseStatus != RParseStatus.OK || res.Error != null || res.Result == null) {
                Debug.Fail(".rtvs.traceback() failed");
                return;
            }

            JArray jFrames;
            try {
                jFrames = JArray.Parse(res.Result);
            } catch (JsonException) {
                Debug.Fail("Failed to parse JSON returned by .rtvs.traceback()");
                return;
            }

            string callingExpression = null;
            int i = 0;
            foreach (JObject jFrame in jFrames) {
                DebugStackFrame stackFrame;
                try {
                    string fileName = (string)jFrame["filename"];
                    int? lineNumber = (int?)(double?)jFrame["linenum"];
                    bool isGlobal = (bool)jFrame["is_global"];

                    stackFrame = new DebugStackFrame(this, i, fileName, lineNumber, callingExpression, isGlobal);

                    callingExpression = (string)jFrame["call"];
                } catch (JsonException ex) {
                    Debug.Fail(ex.ToString());
                    stackFrame = new DebugStackFrame(this, i, null, null, null, false);
                    callingExpression = null;
                }

                _stackFrames.Add(stackFrame);
                ++i;
            }

            _stackFrames.Reverse();
        }

        private bool IsInBrowser(IReadOnlyList<IRContext> contexts) {
            return contexts.SkipWhile(context => context.CallFlag.HasFlag(RContextType.Restart))
                .FirstOrDefault()?.CallFlag.HasFlag(RContextType.Browser) == true;
        }

        private void RSession_BeforeRequest(object sender, RRequestEventArgs e) {
            if (IsInBrowser(e.Contexts)) {
                RSession.BeginEvaluationAsync().ContinueWith(async t => {
                    using (var eval = await t) {
                        await FetchStackFrames(eval).ConfigureAwait(false);
                    }

                    if (_stepTcs != null) {
                        var stepTcs = _stepTcs;
                        _stepTcs = null;
                        stepTcs.TrySetResult(null);
                    } else {
                        BreakpointHit?.Invoke(this, EventArgs.Empty);
                    }

                    Paused?.Invoke(this, EventArgs.Empty);
                });
            } else {
                Paused?.Invoke(this, EventArgs.Empty);
            }
        }

        private void RSession_AfterRequest(object sender, RRequestEventArgs e) {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }

    public class REvaluationException : Exception {
        public REvaluationResult Result { get; }

        public REvaluationException(REvaluationResult result) {
            Result = result;
        }
    }
}