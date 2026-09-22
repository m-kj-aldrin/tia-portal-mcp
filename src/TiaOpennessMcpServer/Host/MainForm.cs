using System;
using System.Drawing;
using System.Windows.Forms;

namespace TiaOpennessMcpServer.Host;

public class MainForm : Form
{
    private readonly NotifyIcon  _tray;
    private bool _closeForReal;

    public MainForm(Uri dashboardUri)
    {
        Text = "TIA Portal MCP";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(580, 200);
        Size = new Size(680, 220);
        var link = new LinkLabel
        {
            Text = "TIA Portal MCP — open dashboard in your browser\n" + dashboardUri.AbsoluteUri,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter
        };
        link.LinkClicked += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dashboardUri.AbsoluteUri, UseShellExecute = true
        });
        Controls.Add(link);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open",  null, (_, _) => Restore());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit",  null, (_, _) => { _closeForReal = true; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Text             = "TIA Portal MCP",
            Visible          = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseDoubleClick += (_, _) => Restore();

        Resize      += OnResize;
        FormClosing += OnClosing;
    }

    private void OnResize(object sender, EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized) return;
        Hide();
        _tray.ShowBalloonTip(1500, "TIA Portal MCP",
            "Still running in the background. Double-click to reopen.", ToolTipIcon.Info);
    }

    private void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (_closeForReal || e.CloseReason != CloseReason.UserClosing) return;
        e.Cancel = true;
        Hide();
    }

    private void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void RequestExit()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RequestExit));
            return;
        }

        _closeForReal = true;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
