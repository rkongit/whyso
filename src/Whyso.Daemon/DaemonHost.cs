using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whyso.Activities;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Daemon;

public sealed class AppState
{
    public required Store Store { get; init; }
    public required Config Config { get; init; }

    private IEventSink? _sink;

    /// <summary>Where validated events go. Defaults to the local store.</summary>
    public IEventSink Sink
    {
        get => _sink ??= new LocalStoreSink(Store, Config.ProjectId);
        init => _sink = value;
    }

    /// <summary>Actor stamped on events that arrive without one (v1.1 envelope).</summary>
    public EventActor LocalActor { get; init; } = EventActor.Local();
}

public static class DaemonHost
{
    public static WebApplication BuildApp(AppState state, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.WebHost.UseUrls(); // URLs set by caller via Listen
        builder.Services.AddSingleton(state);

        var app = builder.Build();

        app.MapGet("/v1/health", (AppState s) => Results.Json(new
        {
            status = "ok",
            project_id = s.Config.ProjectId,
        }));

        app.MapPost("/v1/events", async (HttpContext ctx, AppState s) =>
        {
            AgentEvent? event_;
            try
            {
                event_ = await System.Text.Json.JsonSerializer.DeserializeAsync<AgentEvent>(
                    ctx.Request.Body, EventJson.Options);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (event_ is null)
            {
                return Results.Json(new { error = "invalid body" }, statusCode: StatusCodes.Status400BadRequest);
            }

            event_.Sequence = null;

            if (!Protocol.TryValidateEnvelope(event_, out var validationError))
            {
                return Results.Json(
                    new { error = validationError!.Message },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // v1.1 defaults: who produced it, which project, and private unless the client says otherwise.
            event_.Actor ??= s.LocalActor;
            event_.ProjectId ??= s.Config.ProjectId;
            event_.Visibility ??= Protocol.VisibilityPrivate;

            try
            {
                var outcome = await s.Sink.AppendAsync(event_);
                if (outcome is AppendOutcome.Inserted
                    && event_.EventType == EventType.SessionEnded)
                {
                    try
                    {
                        await WorkNodeIndex.ProjectSessionByIdAsync(s.Store, event_.SessionId);
                    }
                    catch
                    {
                        // Projection is best-effort; never fail ingest.
                    }
                }

                return outcome switch
                {
                    AppendOutcome.Inserted inserted => Results.Json(
                        inserted.Event, EventJson.Options, statusCode: StatusCodes.Status201Created),
                    AppendOutcome.Duplicate duplicate => Results.Json(
                        duplicate.Event, EventJson.Options, statusCode: StatusCodes.Status200OK),
                    _ => Results.Json(new { error = "unknown outcome" }, statusCode: 500),
                };
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        return app;
    }

    public static async Task ServeAsync(
        AppState state,
        string bind,
        CancellationToken cancellationToken = default,
        Action? onStarted = null)
    {
        if (!IPEndPoint.TryParse(bind, out var endpoint))
        {
            throw new InvalidOperationException($"failed to bind {bind}: invalid address");
        }

        var app = BuildApp(state);
        app.Urls.Clear();
        app.Urls.Add($"http://{endpoint}");
        if (onStarted is not null)
        {
            // Fires only once the port is bound, so a daemon that loses a start race never runs it.
            app.Lifetime.ApplicationStarted.Register(onStarted);
        }

        await app.RunAsync(cancellationToken);
    }
}
