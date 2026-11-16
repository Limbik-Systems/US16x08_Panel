using System;
using System.Windows;
using System.Windows.Input;
using US16x08_AbletonPanel.Models;
using US16x08_AbletonPanel.ViewModels;

namespace US16x08_AbletonPanel;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        MidiPatchbay.RouteToggleRequested = (src, dst, toggleIfExisting) =>
        {
            _viewModel.HandleMidiRouteToggle(src, dst, toggleIfExisting);
        };
        MidiPatchbay.PortPanicRequested = portName =>
        {
            _viewModel.PortMidiPanicCommand.Execute(portName);
        };
        MidiPatchbay.PortAuditionRequested = portName =>
        {
            _viewModel.AuditionMidiPortCommand.Execute(portName);
        };
        MidiPatchbay.PortChannelCycleRequested = (portName, delta) =>
        {
            _viewModel.HandlePortChannelCycle(portName, delta);
        };
        MidiPatchbay.PortDisconnectAllRequested = (portName, isInput) =>
        {
            _viewModel.HandlePortDisconnectAll(portName, isInput);
        };
        _viewModel.MidiVisualsChanged += () => MidiPatchbay.InvalidateVisual();

        Closed += OnWindowClosed;
    }

    // ---- System-tray (notification area) support ----
    // Minimize and Close both hide the window to the tray; the app keeps running. Exit is only from the
    // tray menu, which does a real Close() so OnWindowClosed disposes the view model and frees MIDI ports.
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _forceExit;
    private bool _trayHintShown;

    // Lets external tools (build_v2.cmd, the installer) ask a tray-resident panel to really exit, since a
    // graceful taskkill now only hides it to the tray. They set this named event; we exit when it fires.
    private const string QuitSignalName = "US16x08_Panel_QuitSignal_v1";
    private System.Threading.EventWaitHandle? _quitSignal;
    private System.Threading.RegisteredWaitHandle? _quitReg;

    /// <summary>Called once at startup in normal mode (never in --self-test, which exits via Shutdown).</summary>
    public void EnableTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "TASCAM US-16x08 // DSP & MIDI Console",
            Icon = LoadTrayIcon(),
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Console", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        StateChanged += OnStateChangedForTray;
        Closing += OnClosingForTray;

        try
        {
            _quitSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, QuitSignalName);
            _quitReg = System.Threading.ThreadPool.RegisterWaitForSingleObject(
                _quitSignal,
                (_, _) => Dispatcher.BeginInvoke(new Action(ExitFromTray)),
                null, -1, executeOnlyOnce: true);
        }
        catch { /* named-event unavailable; taskkill fallback still works */ }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/US16x08_Panel.ico"));
            if (info != null)
                using (info.Stream)
                    return new System.Drawing.Icon(info.Stream, new System.Drawing.Size(16, 16));
        }
        catch { }
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application; }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private void OnStateChangedForTray(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void OnClosingForTray(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceExit) return;   // real exit chosen from the tray menu
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();                   // a hidden window has no taskbar button
        if (!_trayHintShown && _trayIcon != null)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(3000, "US-16x08 Console",
                "Still running here. Double-click to reopen, or right-click for Exit.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // pull to the foreground
    }

    private void ExitFromTray()
    {
        _forceExit = true;
        Close();   // OnClosingForTray allows it; OnWindowClosed disposes the view model
    }

    private void OnTrackStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackChannelModel track)
        {
            _viewModel.SelectedTrack = track;
        }
    }

    // Collapse this channel to a thin strip (per-channel hide).
    private void OnCollapseTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackChannelModel track)
            track.IsCollapsed = true;
        e.Handled = true;   // don't also select via the strip's MouseLeftButtonDown
    }

    // Click a collapsed thin strip to expand it back (and select it).
    private void OnExpandTrackClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TrackChannelModel track)
        {
            track.IsCollapsed = false;
            _viewModel.SelectedTrack = track;
        }
        e.Handled = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _quitReg?.Unregister(null);
        _quitSignal?.Dispose();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _viewModel.Dispose();
    }
}
