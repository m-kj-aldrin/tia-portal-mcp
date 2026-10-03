using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TiaOpennessMcpServer.Host;

// Runs a Windows message loop for the tray without a visible form or taskbar entry.
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Uri _dashboardUri;
    private readonly WindowsFormsSynchronizationContext _dispatcher;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _tray;
    private int _exitRequested;
    private int _disposed;

    public event EventHandler? Ready;

    public TrayApplicationContext(Uri dashboardUri, Action requestShutdown)
    {
        _dashboardUri = dashboardUri;
        WindowsFormsSynchronizationContext? dispatcher = null;
        ContextMenuStrip? menu = null;
        NotifyIcon? tray = null;
        try
        {
            dispatcher = new WindowsFormsSynchronizationContext();
            menu = new ContextMenuStrip();
            menu.Items.Add("Open dashboard", null, (_, _) => OpenDashboard());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => requestShutdown());
            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "TIA Portal MCP",
                ContextMenuStrip = menu
            };
            tray.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) OpenDashboard();
            };
            tray.Visible = true;
            _dispatcher = dispatcher;
            _menu = menu;
            _tray = tray;
            Application.Idle += OnFirstIdle;
        }
        catch
        {
            tray?.Dispose();
            menu?.Dispose();
            dispatcher?.Dispose();
            throw;
        }
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void OpenDashboard()
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo
            {
                FileName = _dashboardUri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            _tray.ShowBalloonTip(5000, "Unable to open dashboard", ex.Message, ToolTipIcon.Error);
        }
    }

    public void RequestExit()
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        // Post even on the tray thread: cancellation may arrive before Application.Run starts.
        try { _dispatcher.Post(_ => ExitThread(), null); }
        catch (InvalidAsynchronousStateException) when (Volatile.Read(ref _disposed) != 0) { }
        catch (InvalidOperationException) when (Volatile.Read(ref _disposed) != 0) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Application.Idle -= OnFirstIdle;
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
