namespace TiaOpennessMcpServer.Host;

// Browser origins for this listener only. Do not trust the incoming Host header or
// resolve arbitrary hostnames: either could turn the check into a DNS-rebinding bypass.
internal sealed class LoopbackOriginPolicy
{
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);

    public LoopbackOriginPolicy(Uri listenerUri)
    {
        if (!listenerUri.IsAbsoluteUri || listenerUri.Scheme != Uri.UriSchemeHttp || !listenerUri.IsLoopback)
            throw new ArgumentException("Origin policy requires an HTTP loopback listener.", nameof(listenerUri));

        foreach (var host in new[] { "127.0.0.1", "localhost" })
            _allowed.Add(new UriBuilder(Uri.UriSchemeHttp, host, listenerUri.Port).Uri.GetLeftPart(UriPartial.Authority));
    }

    // Non-browser MCP clients can omit Origin. Browser-supplied opaque/null,
    // malformed and foreign origins must match neither entry.
    public bool Allows(string? origin) => origin == null || _allowed.Contains(origin);
}
