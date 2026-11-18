using System.Threading.RateLimiting;
using Altong.Slack.Contracts;
using Altong.Slack.Server;
using Microsoft.AspNetCore.RateLimiting;

namespace Altong.Slack.Server;

public static class SlackServerApplication
{
    public static WebApplication Create(string[] args, SlackServerOptions? settings = null, HttpClient? slackHttp = null, bool startWorkers = true)
    {
        var builder = WebApplication.CreateBuilder(args);
        settings ??= SlackServerOptions.FromEnvironment(builder.Environment.IsDevelopment());
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
        // OAuth codes/state and Slack socket URLs must never enter request/exception logs.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<EncryptedSlackStore>();
        builder.Services.AddSingleton(provider => new SlackWebApi(settings, provider.GetRequiredService<EncryptedSlackStore>(), slackHttp));
        builder.Services.AddSingleton<SlackReceiverHealth>();
        builder.Services.AddSingleton<SlackEventRouter>();
        if (startWorkers)
        {
            builder.Services.AddHostedService<SlackSocketWorker>();
            builder.Services.AddHostedService<SlackRouterWorker>();
        }
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("pairing", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("api", context =>
            {
                var store = context.RequestServices.GetRequiredService<EncryptedSlackStore>();
                var device = Device(context, store);
                string partition = "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                if (device is not null) partition = "device:" + device.Id;
                else if (context.GetRouteValue("id") is string id
                    && store.PairingStatus(id, context.Request.Headers["X-Altong-Pairing-Proof"].ToString()) is not null)
                    partition = "pairing:" + id;
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                app.Logger.LogWarning("Slack request failed ({Type}).", ex.GetType().Name);
                if (!context.Response.HasStarted)
                { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "service_unavailable" }); }
            }
        });
        app.UseRateLimiter();

        app.MapGet("/health", (SlackReceiverHealth health) => Results.Ok(new { receiverConnected = health.Connected }));
        app.MapPost("/v1/slack/connections", (BeginSlackConnection body, EncryptedSlackStore store, SlackWebApi api) =>
        {
            if (!SlackConnectionProof.IsValid(body.Challenge)) return Results.BadRequest();
            string state = SlackConnectionProof.NewSecret();
            var attempt = store.BeginPairing(state, body.Challenge);
            return Results.Ok(new SlackConnectionAttempt(attempt.Id, api.AuthorizationUrl(state), attempt.Expires));
        }).RequireRateLimiting("pairing");

        app.MapGet("/oauth/slack/callback", async (HttpContext context, EncryptedSlackStore store, SlackWebApi api) =>
        {
            string state = context.Request.Query["state"].ToString();
            string? id = store.ConsumeState(state);
            if (id is null) return Results.Text("연결 요청이 만료되었거나 유효하지 않습니다. 알통에서 다시 연결해 주세요.", statusCode: 400);
            if (context.Request.Query.ContainsKey("error"))
            {
                store.FailPairing(id, "denied");
                return Results.Text("Slack 연결을 취소했습니다. 알통으로 돌아가 주세요.");
            }
            string code = context.Request.Query["code"].ToString();
            if (code.Length is 0 or > 4096) { store.FailPairing(id, "failed"); return Results.Text("Slack 연결에 실패했습니다.", statusCode: 400); }
            try
            {
                var credential = await api.ExchangeAsync(code, context.RequestAborted);
                await api.CompletePairingAsync(id, credential, context.RequestAborted);
                return Results.Text("Slack 연결이 완료되었습니다. 이 창을 닫고 알통으로 돌아가 주세요.");
            }
            catch (Exception ex) when (ex is SlackServiceException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                store.FailPairing(id, "failed");
                return Results.Text("Slack 연결을 완료하지 못했습니다. 알통에서 다시 연결해 주세요.", statusCode: 400);
            }
        });

        app.MapGet("/v1/slack/connections/{id}", (string id, HttpContext context, EncryptedSlackStore store) =>
        {
            string proof = context.Request.Headers["X-Altong-Pairing-Proof"].ToString();
            var result = store.PairingStatus(id, proof);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting("api");

        app.MapDelete("/v1/slack/connections/{id}", async (string id, HttpContext context, EncryptedSlackStore store, SlackWebApi api) =>
        {
            string proof = context.Request.Headers["X-Altong-Pairing-Proof"].ToString();
            if (store.PairingStatus(id, proof) is null) return Results.NotFound();
            if (store.CancelPairing(id, proof) is { } device) await api.DisconnectAsync(device, context.RequestAborted);
            return Results.NoContent();
        }).RequireRateLimiting("api");

        app.MapGet("/v1/slack/session", (HttpContext context, EncryptedSlackStore store, SlackReceiverHealth health) =>
        {
            var device = Device(context, store);
            var installation = device is null ? null : store.Installation(device.UserId);
            return installation is null ? Results.Unauthorized() : Results.Ok(new SlackSessionStatus(installation.UserId,
                installation.Credential.Account.Account, installation.Credential.Account.Workspace, health.Connected));
        }).RequireRateLimiting("api");

        app.MapGet("/v1/slack/messages", async (HttpContext context, EncryptedSlackStore store, SlackReceiverHealth health) =>
        {
            var device = Device(context, store); if (device is null) return Results.Unauthorized();
            for (int i = 0; i < 20; i++)
            {
                if (Device(context, store) is null) return Results.Unauthorized();
                var messages = store.Deliveries(device);
                if (messages.Length > 0 || !health.Connected) return Results.Ok(new SlackDeliveryBatch(health.Connected, messages));
                await Task.Delay(1000, context.RequestAborted);
            }
            return Results.Ok(new SlackDeliveryBatch(health.Connected, []));
        }).RequireRateLimiting("api");

        app.MapPost("/v1/slack/messages/ack", (AcknowledgeSlackDeliveries body, HttpContext context, EncryptedSlackStore store) =>
        {
            var device = Device(context, store); if (device is null) return Results.Unauthorized();
            if (body.Ids is null || body.Ids.Length > 100 || body.Ids.Any(id => !SlackConnectionProof.IsValid(id))) return Results.BadRequest();
            store.Acknowledge(device, body.Ids); return Results.NoContent();
        }).RequireRateLimiting("api");

        app.MapDelete("/v1/slack/session", async (HttpContext context, EncryptedSlackStore store, SlackWebApi api) =>
        {
            if (Device(context, store) is { } device) await api.DisconnectAsync(device, context.RequestAborted);
            return Results.NoContent(); // Idempotent after a lost successful disconnect response.
        }).RequireRateLimiting("api");

        return app;

    }

    private static SlackDevice? Device(HttpContext context, EncryptedSlackStore store)
    {
        string header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? store.Authenticate(header[7..]) : null;
    }
}
