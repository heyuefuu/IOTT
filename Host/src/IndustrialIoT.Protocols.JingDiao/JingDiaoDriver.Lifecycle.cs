namespace IndustrialIoT.Protocols.JingDiao;

using IndustrialIoT.Domain.Enums;
using Microsoft.Extensions.Logging;

public sealed partial class JingDiaoDriver
{
    private async Task CloseSessionAsync(CancellationToken ct)
    {
        var previousSession = sessionId;
        sessionId = "";
        if (client is null || string.IsNullOrEmpty(previousSession)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var result = await client.DisconnectAsync(previousSession, deadline.Token);
            if (result.ReturnCode != 0 && result.ReturnCode != -404)
                logger.LogWarning("JingDiao session disconnect failed: {Error}", result.ErrorMessage);
        }
        catch (Exception error) { logger.LogWarning(error, "JingDiao session disconnect failed during cleanup"); }
    }

    private async Task ReleaseConnectionAsync(CancellationToken ct)
    {
        try { await CloseSessionAsync(ct); }
        finally
        {
            connectRequest = null;
            try
            {
                if (injectedClient is null && client is IDisposable ownedClient) ownedClient.Dispose();
            }
            finally
            {
                client = null;
                var lease = shimLease;
                shimLease = null;
                try { lease?.Dispose(); }
                finally { SetState(ConnectionState.Disconnected); }
            }
        }
    }

    private async Task<bool> RestoreSessionAsync(CancellationToken ct)
    {
        if (injectedClient is null)
            await JingDiaoShimLease.WaitForReadyAsync(options!.ShimBaseUri,
                TimeSpan.FromMilliseconds(connectRequest!.TimeoutMs), ct);
        await CloseSessionAsync(ct);
        ct.ThrowIfCancellationRequested();
        var result = await client!.ConnectAsync(connectRequest!, ct);
        if (result.ReturnCode != 0 || string.IsNullOrWhiteSpace(result.SessionId))
            throw new IOException(result.ErrorMessage ?? $"JingDiao session recovery failed: {result.ReturnCode}");
        sessionId = result.SessionId;
        logger.LogInformation("JingDiao session restored");
        return true;
    }
}
