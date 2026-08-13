using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Worktree.Activities;
using Worktree.Events;
using Worktree.Storage;

namespace Worktree.Daemon;

public sealed class AppState
{
    public required Store Store { get; init; }
    public required Config Config { get; init; }
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

            try
            {
                var outcome = await s.Store.AppendEventAsync(event_, s.Config.ProjectId);
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

    public static async Task ServeAsync(AppState state, string bind, CancellationToken cancellationToken = default)
    {
        if (!IPEndPoint.TryParse(bind, out var endpoint))
        {
            throw new InvalidOperationException($"failed to bind {bind}: invalid address");
        }

        var app = BuildApp(state);
        app.Urls.Clear();
        app.Urls.Add($"http://{endpoint}");
        await app.RunAsync(cancellationToken);
    }
}
