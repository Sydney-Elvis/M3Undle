using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace M3Undle.Web.Tests.Notifications;

public sealed record MatrixRequestRecord(string Method, string Path, string? Authorization, string Body);

/// <summary>A minimal Matrix homeserver speaking just the endpoints the notification provider uses, with scriptable faults.</summary>
internal sealed class FakeMatrixHomeserver : IAsyncDisposable
{
    public const string Token = "syt_test_token";
    public const string RoomId = "!room:test";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Func<HttpContext, Task<bool>>> _sendScript = new();
    private readonly ConcurrentDictionary<string, string> _eventsByTransaction = new();
    private int _eventCounter;

    public string BaseUrl { get; }
    public string UserId { get; set; } = "@bot:test";
    public string? DeviceId { get; set; } = "DEV1";
    public bool Joined { get; set; } = true;
    public bool Encrypted { get; set; }
    public string? PowerLevelsJson { get; set; }
    public ConcurrentQueue<MatrixRequestRecord> Requests { get; } = new();
    public ConcurrentBag<string> RedirectTargetHits { get; } = [];
    public int DistinctEvents => _eventsByTransaction.Count;
    public List<string> SentBodies { get; } = [];

    private FakeMatrixHomeserver(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeMatrixHomeserver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeMatrixHomeserver? server = null;

        app.Use(async (context, next) =>
        {
            var body = string.Empty;
            context.Request.EnableBuffering();
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
                body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            server!.Requests.Enqueue(new MatrixRequestRecord(context.Request.Method, context.Request.Path.Value ?? string.Empty,
                context.Request.Headers.Authorization.ToString(), body));
            await next();
        });

        app.MapGet("/elsewhere", (HttpContext c) =>
        {
            server!.RedirectTargetHits.Add(c.Request.Headers.Authorization.ToString());
            return Results.Ok();
        });
        app.MapPut("/elsewhere", (HttpContext c) =>
        {
            server!.RedirectTargetHits.Add(c.Request.Headers.Authorization.ToString());
            return Results.Ok();
        });

        app.MapGet("/_matrix/client/v3/account/whoami", (HttpContext c) => server!.Guard(c) ??
            Results.Json(server.DeviceId is null ? new { user_id = server.UserId } : (object)new { user_id = server.UserId, device_id = server.DeviceId }));
        app.MapGet("/_matrix/client/v3/joined_rooms", (HttpContext c) => server!.Guard(c) ??
            Results.Json(new { joined_rooms = server.Joined ? new[] { RoomId } : Array.Empty<string>() }));
        app.MapGet("/_matrix/client/v3/rooms/{room}/state/m.room.encryption", (HttpContext c) => server!.Guard(c) ??
            (server.Encrypted ? Results.Json(new { algorithm = "m.megolm.v1.aes-sha2" }) : Results.Json(new { errcode = "M_NOT_FOUND" }, statusCode: 404)));
        app.MapGet("/_matrix/client/v3/rooms/{room}/state/m.room.power_levels", (HttpContext c) => server!.Guard(c) ??
            (server.PowerLevelsJson is null ? Results.Json(new { errcode = "M_NOT_FOUND" }, statusCode: 404) : Results.Content(server.PowerLevelsJson, "application/json")));
        app.MapPut("/_matrix/client/v3/rooms/{room}/send/m.room.message/{txn}", async (HttpContext c, string txn) =>
        {
            if (server!.Guard(c) is { } denied)
                return denied;

            if (server._sendScript.TryDequeue(out var step) && await step(c))
                return Results.Empty;

            using var document = await JsonDocument.ParseAsync(c.Request.Body);
            lock (server.SentBodies)
                server.SentBodies.Add(document.RootElement.GetRawText());
            var eventId = server._eventsByTransaction.GetOrAdd(txn, _ => $"$event{Interlocked.Increment(ref server._eventCounter)}");
            return Results.Json(new { event_id = eventId });
        });

        await app.StartAsync();
        var address = app.Urls.First();
        server = new FakeMatrixHomeserver(app, address);
        return server;
    }

    private IResult? Guard(HttpContext context) =>
        context.Request.Headers.Authorization == $"Bearer {Token}"
            ? null
            : Results.Json(new { errcode = "M_UNKNOWN_TOKEN", error = "Invalid access token" }, statusCode: 401);

    /// <summary>Queues a custom reply for the next send. Return true after writing a response yourself.</summary>
    public FakeMatrixHomeserver NextSend(Func<HttpContext, Task<bool>> step)
    {
        _sendScript.Enqueue(step);
        return this;
    }

    public FakeMatrixHomeserver NextSendStatus(int status, string? json = null, string? retryAfterHeader = null) =>
        NextSend(async c =>
        {
            c.Response.StatusCode = status;
            if (retryAfterHeader is not null)
                c.Response.Headers.RetryAfter = retryAfterHeader;
            c.Response.ContentType = "application/json";
            await c.Response.WriteAsync(json ?? "{}");
            return true;
        });

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
