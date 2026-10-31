namespace KasNetWallet.Core.Rpc;

/// <summary>D33: one clear status line for node/connection failures. Timeouts and cancellations (TaskCanceledException
/// from the per-call/overall timeout) become "node not reachable" instead of "A task was canceled."</summary>
public static class NodeErrorText
{
    public static string Describe(Exception ex, string url)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is ArgumentException) return "Error: " + e.Message;
        if (ex is OperationCanceledException or TimeoutException)
            return $"Connection failed: node {url} did not answer in time. Check the URL (JSON wRPC, e.g. 18110 mainnet) and that the node is reachable.";
        if (ex is System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException or IOException)
            return $"Connection failed: cannot reach {url} ({ex.Message}).";
        return "Error: " + ex.Message;
    }
}
