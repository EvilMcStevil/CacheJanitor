using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CacheJanitor;

/// <summary>
/// Answers status and trim-now requests from the tray app on \\.\pipe\CacheJanitor. Local signed-in users may
/// connect; network clients may not. Requests can only read status or ask for a pass under the configured
/// limits, so nothing here needs more than an ordinary user's rights.
/// </summary>
public sealed class PipeServer(Worker worker, ILogger<PipeServer> log) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream next;
        try
        {
            next = Create(first: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            log.LogError("Could not open pipe {Pipe}; another process already owns it, so the tray app cannot show status. {Message}",
                AppPaths.PipeName, ex.Message);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var current = next;
            try
            {
                await current.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await current.DisposeAsync();
                break;
            }
            catch (IOException ex)
            {
                log.LogDebug("Pipe connection failed: {Message}", ex.Message);
                await current.DisposeAsync();
                next = Create(first: false);
                continue;
            }

            // Open the next instance before serving this one, so the name is never free for another process to take.
            next = Create(first: false);
            _ = ServeAsync(current, stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using var _ = pipe;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(RequestTimeout);
        try
        {
            var request = await PipeProtocol.ReadLineAsync<PipeRequest>(pipe, 64 * 1024, cts.Token);
            var response = Handle(request);
            await PipeProtocol.WriteLineAsync(pipe, response, cts.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogDebug("Pipe request abandoned: {Message}", ex.Message);
        }
    }

    private PipeResponse Handle(PipeRequest? request)
    {
        switch (request?.Command?.ToLowerInvariant())
        {
            case "status":
                return new PipeResponse { Ok = true, Status = worker.GetStatus() };
            case "trim":
                return worker.RequestTrim(request.Path)
                    ? new PipeResponse { Ok = true }
                    : new PipeResponse { Ok = false, Error = $"{request.Path} is not a configured cache." };
            default:
                return new PipeResponse { Ok = false, Error = $"Unknown command '{request?.Command}'." };
        }
    }

    private static NamedPipeServerStream Create(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(AppPaths.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options,
            inBufferSize: 0, outBufferSize: 0, security);
    }
}
