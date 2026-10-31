using KasNetWallet.Core.Rpc;

namespace KasNetWallet.Core.Tests;

/// <summary>D33: timeouts/cancellations on connect become one clear status line (no "A task was canceled.").</summary>
public class NodeErrorTextTests
{
    const string Url = "ws://eva.kaspa.green:18110";

    [Fact]
    public void TaskCanceled_IsReportedAsUnreachable()
    {
        var t = NodeErrorText.Describe(new TaskCanceledException(), Url);
        Assert.Contains("Connection failed", t); Assert.Contains(Url, t); Assert.DoesNotContain("canceled", t);
    }

    [Fact]
    public void Timeout_FromResilientNode_IsReportedAsUnreachable() =>
        Assert.Contains("did not answer in time", NodeErrorText.Describe(new TimeoutException("Node unavailable after 1 attempt(s)", new TaskCanceledException()), Url));

    [Fact]
    public void ArgumentException_KeepsItsMessage() =>
        Assert.Contains("Borsh", NodeErrorText.Describe(Assert.Throws<ArgumentException>(() => ResilientWalletNode.ValidateUrl("ws://eva.kaspa.green:17110")), Url));

    [Fact]
    public async Task UnreachableNode_FailsBounded_NoTightLoop()
    {
        var attempts = 0;
        var node = new ResilientWalletNode(async ct => { attempts++; await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); },
            TimeSpan.FromMilliseconds(50), maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => node.GetStatusAsync());
        Assert.Equal(3, attempts);
    }
}
