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

    public async Task<T?> ReadJson<T>(HttpListenerRequest request) where T : class
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, _json);
    }
}
