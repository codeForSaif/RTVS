﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.Common.Core.Disposables;
using Microsoft.R.Components.ConnectionManager;
using Microsoft.R.Components.History;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.InteractiveWorkflow.Implementation;
using Microsoft.R.Components.PackageManager;
using Microsoft.R.Components.Plots;
using Microsoft.R.Components.Settings;
using Microsoft.VisualStudio.R.Package.Shell;

namespace Microsoft.VisualStudio.R.Package.Repl {
    [Export(typeof(IRInteractiveWorkflowProvider))]
    internal class VsRInteractiveWorkflowProvider : IRInteractiveWorkflowProvider, IDisposable {
        private readonly DisposableBag _disposableBag = DisposableBag.Create<VsRInteractiveWorkflowProvider>();

        private readonly IConnectionManagerProvider _connectionsProvider;
        private readonly IRHistoryProvider _historyProvider;
        private readonly IRPackageManagerProvider _packagesProvider;
        private readonly IRPlotManagerProvider _plotsProvider;
        private readonly IActiveWpfTextViewTracker _activeTextViewTracker;
        private readonly IDebuggerModeTracker _debuggerModeTracker;
        private readonly IApplicationShell _shell;
        private readonly IRSettings _settings;

        private Lazy<IRInteractiveWorkflow> _instanceLazy;

        [ImportingConstructor]
        public VsRInteractiveWorkflowProvider(IConnectionManagerProvider connectionsProvider
            , IRHistoryProvider historyProvider
            , IRPackageManagerProvider packagesProvider
            , IRPlotManagerProvider plotsProvider
            , IActiveWpfTextViewTracker activeTextViewTracker
            , IDebuggerModeTracker debuggerModeTracker
            , IApplicationShell shell
            , IRSettings settings) {

            _connectionsProvider = connectionsProvider;
            _historyProvider = historyProvider;
            _packagesProvider = packagesProvider;
            _plotsProvider = plotsProvider;
            _activeTextViewTracker = activeTextViewTracker;
            _debuggerModeTracker = debuggerModeTracker;
            _shell = shell;
            _settings = settings;

            _shell.Terminating += OnApplicationTerminating;
        }

        private void OnApplicationTerminating(object sender, EventArgs e) {
            Dispose();
        }

        public void Dispose() {
            _disposableBag.TryDispose();
        }

        public IRInteractiveWorkflow GetOrCreate() {
            _disposableBag.ThrowIfDisposed();

            Interlocked.CompareExchange(ref _instanceLazy, new Lazy<IRInteractiveWorkflow>(CreateRInteractiveWorkflow), null);
            return _instanceLazy.Value;
        }

        public IRInteractiveWorkflow Active => (_instanceLazy != null && _instanceLazy.IsValueCreated) ? _instanceLazy.Value : null;

        private IRInteractiveWorkflow CreateRInteractiveWorkflow() {
            _disposableBag.Add(DisposeInstance);
            return new RInteractiveWorkflow(_connectionsProvider, _historyProvider, _packagesProvider, _plotsProvider, _activeTextViewTracker, _debuggerModeTracker, _shell, _settings);
        }

        private void DisposeInstance() {
            var lazy = Interlocked.Exchange(ref _instanceLazy, null);
            if (lazy != null && lazy.IsValueCreated) {
                lazy.Value.Dispose();
            }
        }
    }
}