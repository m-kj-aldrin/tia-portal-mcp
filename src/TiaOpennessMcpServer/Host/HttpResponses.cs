using System.Net;
using System.Text;
using System.Text.Json;

namespace TiaOpennessMcpServer.Host;

internal sealed class HttpResponses
{
    private readonly JsonSerializerOptions _json;
    public HttpResponses(JsonSerializerOptions json) => _json = json;

    public async Task Json(HttpListenerResponse response, object? data, int status = 200)
    {
        response.StatusCode = status;
        await WriteBytes(response, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, _json)), "application/json; charset=utf-8");
    }

    public async Task WriteBytes(HttpListenerResponse response, byte[] bytes, string contentType)
    {
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        try { await response.OutputStream.WriteAsync(bytes, 0, bytes.Length); }
        finally { response.Close(); }
    }
}
