using System.Net;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using TiaOpennessMcpServer.Dashboard;
using TiaOpennessMcpServer.Mcp;

namespace TiaOpennessMcpServer.Host;

// Owns transport and process lifecycle; endpoint modules own their respective routes.
internal sealed class HttpHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Uri _baseUri;
    private readonly string? _controlToken;
    private readonly HttpResponses _http;
    private readonly McpEndpoint _mcp;
    private readonly DashboardEndpoints _dashboard;
    private readonly TaskCompletionSource<bool> _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TrayApplicationContext? _trayContext;
    private Exception? _trayFailure;
    private int _shutdownRequested;

    public HttpHost(Uri baseUri, string? controlToken, HttpResponses http, McpEndpoint mcp, DashboardEndpoints dashboard)
    {
        if (!baseUri.IsLoopback)
            throw new InvalidOperationException("The dashboard and HTTP MCP listener must bind to a loopback address.");
        _baseUri = baseUri;
        _controlToken = controlToken;
        _http = http;
        _mcp = mcp;
        _dashboard = dashboard;
        _listener.Prefixes.Add(baseUri.AbsoluteUri);
    }

    public async Task RunAsync()
    {
        // The Windows shell has its own UI thread. All Openness work uses the shared engineering STA.
        var trayThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                using var tray = new TrayApplicationContext(_baseUri, RequestShutdown);
                tray.Ready += (_, _) => _hostReady.TrySetResult(true);
                Volatile.Write(ref _trayContext, tray);
                if (Volatile.Read(ref _shutdownRequested) != 0) tray.RequestExit();
                Application.Run(tray);
            }
            catch (Exception ex) { _trayFailure = ex; }
            finally
            {
                Volatile.Write(ref _trayContext, null);
                _hostReady.TrySetResult(false);
                RequestShutdown();
            }
        }) { IsBackground = false, Name = "TIA MCP tray" };
        trayThread.SetApartmentState(ApartmentState.STA);

        var trayStarted = false;
        try
        {
            _listener.Start();
            Console.CancelKeyPress += OnCancel;
            trayThread.Start();
            trayStarted = true;
            if (await _hostReady.Task)
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    _ = Task.Run(() => HandleAsync(context));
                }
            }
        }
        finally
        {
            RequestShutdown();
            if (trayStarted) await Task.Run(() => trayThread.Join());
            Console.CancelKeyPress -= OnCancel;
        }

        if (_trayFailure != null) ExceptionDispatchInfo.Capture(_trayFailure).Throw();
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.HttpMethod == "OPTIONS") { response.StatusCode = 200; response.Close(); return; }

        var path = (request.RawUrl ?? "/").Split('?')[0].TrimEnd('/');
        if (path.Length == 0) path = "/";
        try
        {
            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/lifecycle/health", StringComparison.OrdinalIgnoreCase))
                await HealthAsync(context);
            else if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/api/lifecycle/stop", StringComparison.OrdinalIgnoreCase))
                await StopAsync(context);
            else if (path == "/mcp") await _mcp.HandleAsync(context);
            else await _dashboard.HandleAsync(context, path);
        }
        catch (Exception ex)
        {
            try { await _http.Json(response, new { error = ex.Message }, 500); }
            catch { /* Client may have disconnected. */ }
        }
    }

    private async Task<bool> AuthorizeLifecycleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        if (string.IsNullOrEmpty(_controlToken))
        { await _http.Json(response, new { error = "Lifecycle control is not enabled for this process." }, 404); return false; }
        var supplied = context.Request.Headers["X-Tia-Mcp-Control-Token"]?.Trim();
        if (!string.Equals(supplied, _controlToken, StringComparison.Ordinal))
        { await _http.Json(response, new { error = "Invalid lifecycle control token." }, 403); return false; }
        if (_hostReady.Task.Status != TaskStatus.RanToCompletion || !_hostReady.Task.Result ||
            Volatile.Read(ref _shutdownRequested) != 0)
        { await _http.Json(response, new { error = "The server host is not ready." }, 503); return false; }
        return true;
    }

    private async Task HealthAsync(HttpListenerContext context)
    {
        if (!await AuthorizeLifecycleAsync(context)) return;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        await _http.Json(context.Response, new
        {
            status = "ready", processId = process.Id,
            executablePath = Path.GetFullPath(process.MainModule!.FileName)
        });
    }

    private async Task StopAsync(HttpListenerContext context)
    {
        if (!await AuthorizeLifecycleAsync(context)) return;

        await _http.Json(context.Response, new { status = "stopping" }, 202);
        RequestShutdown();
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestShutdown();
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        try { Volatile.Read(ref _trayContext)?.RequestExit(); }
        finally
        {
            try { _listener.Stop(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        RequestShutdown();
        _dashboard.Dispose();
        _listener.Close();
    }
}
