using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace SsmsDataAnalyzer.Vsix.Commands
{
    /// <summary>
    /// The "Analyze Data..." top-level menu command (Tools menu). Tier B entry point per
    /// PLAN.md — opens <see cref="ToolWindow.ProfileToolWindow"/>. No Object Explorer
    /// dependency; this is the guaranteed-to-work path.
    /// </summary>
    internal sealed class AnalyzeDataCommand
    {
        private readonly AsyncPackage _package;

        private AnalyzeDataCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            if (commandService == null) throw new ArgumentNullException(nameof(commandService));

            var commandId = new CommandID(PackageGuids.CommandSetGuid, PackageIds.AnalyzeDataCommandId);
            var menuItem = new MenuCommand(Execute, commandId);
            commandService.AddCommand(menuItem);
        }

        public static AnalyzeDataCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Command registration is UI-affinitized. The caller (DataAnalyzerPackage) has
            // already switched to the main thread before calling us, but VSTHRD109 flags
            // ThrowIfNotOnUIThread inside an async method as the wrong tool here — an
            // explicit switch is both the fix and belt-and-suspenders correct regardless of
            // what the caller already did.
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new AnalyzeDataCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Fire-and-forget on the JoinableTaskFactory, per VS threading guidance —
            // ShowToolWindowAsync itself is async and must not be blocked on synchronously.
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await _package.ShowToolWindowAsync(
                    typeof(ToolWindow.ProfileToolWindow),
                    id: 0,
                    create: true,
                    cancellationToken: _package.DisposalToken);

                if (window?.Frame == null)
                {
                    throw new NotSupportedException("Cannot create the Analyze Data tool window.");
                }
            }).FileAndForget("SsmsDataAnalyzer/AnalyzeDataCommand/Execute");
        }
    }
}
