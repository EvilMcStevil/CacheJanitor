using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheJanitor;

/// <summary>
/// One request and one response per connection, each a single line of JSON.
/// Requests: <c>{"command":"status"}</c>, <c>{"command":"trim","path":"D:\\cache"}</c> (path optional: all caches).
/// </summary>
public sealed class PipeRequest
{
    public string Command { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class PipeResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public ServiceStatus? Status { get; set; }
}

public static class PipeProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task WriteLineAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadLineAsync<T>(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        while (buffer.Length < maxBytes)
        {
            int n = await stream.ReadAsync(one, ct);
            if (n == 0) break;
            int newline = Array.IndexOf(one, (byte)'\n', 0, n);
            buffer.Write(one, 0, newline >= 0 ? newline : n);
            if (newline >= 0) break;
        }
        if (buffer.Length == 0) return default;
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), Json);
    }
}

/// <summary>Talks to the running service over its named pipe.</summary>
public static class JanitorClient
{
    public static async Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        await PipeProtocol.WriteLineAsync(pipe, request, cts.Token);
        return await PipeProtocol.ReadLineAsync<PipeResponse>(pipe, 64 * 1024 * 1024, cts.Token)
               ?? new PipeResponse { Ok = false, Error = "The service closed the connection without answering." };
    }

    public static async Task<ServiceStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(new PipeRequest { Command = "status" }, TimeSpan.FromSeconds(5), ct);
        return r.Status;
    }
}
