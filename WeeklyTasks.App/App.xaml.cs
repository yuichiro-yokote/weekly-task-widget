using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using WeeklyTasks.Core;

namespace WeeklyTasks.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showListener;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Arg(string key) { int i = Array.IndexOf(e.Args, key); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        string? dataDir = Arg("--data-dir");
        bool demo = e.Args.Contains("--demo");
        if (demo && dataDir is null) dataDir = Path.Combine(Path.GetTempPath(), "WeeklyTasks-Demo");
        try
        {
            var storage = new Storage(dataDir);
            var settings = storage.LoadSettings();
            if (Arg("--source") is string source) { settings.SourcePath = Path.GetFullPath(source); storage.SaveSettings(settings); }
            if (e.Args.Contains("--refresh-only"))
            {
                try { await new CodexRunner(storage).RefreshAsync(settings.SourcePath, null, CancellationToken.None); Shutdown(0); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(storage.DirectoryPath, "refresh-error.txt"), ex.Message, new UTF8Encoding(false)); Shutdown(1); }
                return;
            }
            string mutexName = "Local\\WeeklyTasks-" + HomeSource.Hash(storage.DirectoryPath)[..16];
            _instance = new Mutex(true, mutexName, out bool first);
            _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, mutexName + "-show");
            if (!first) { _showRequest.Set(); Shutdown(); return; }
            DispatcherUnhandledException += (_, args) =>
            {
                System.Windows.MessageBox.Show("操作に失敗しました。\n" + args.Exception.Message, "1週間のタスク");
                args.Handled = true;
            };
            var window = new MainWindow(storage, settings, demo, Arg("--export-preview"));
            MainWindow = window;
            window.Show();
            _showListener = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => Dispatcher.BeginInvoke(() =>
            { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }), null, Timeout.Infinite, false);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show("起動できませんでした。\n" + ex.Message, "1週間のタスク"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { _showListener?.Unregister(null); _showRequest?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}
