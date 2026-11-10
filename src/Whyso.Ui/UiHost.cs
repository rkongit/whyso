using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whyso.Activities;
using Whyso.Events;
using Whyso.Storage;

namespace Whyso.Ui;

public static class UiHost
{
    public const string DefaultBind = "127.0.0.1:9478";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static WebApplication BuildApp(UiState state, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.WebHost.UseUrls();
        builder.Services.AddSingleton(state);
        builder.Services.AddCors(o =>
        {
            o.AddDefaultPolicy(p => p
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod());
        });

        var app = builder.Build();
        app.UseCors();

        MapApi(app);
        MapSpa(app);

        return app;
    }

    public static async Task ServeAsync(UiState state, string bind, CancellationToken cancellationToken = default)
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

    private static void MapApi(WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/health", (UiState s) => Results.Json(new
        {
            status = "ok",
            project_id = s.Config.ProjectId,
        }, JsonOptions));

        api.MapGet("/project", async (UiState s) =>
        {
            await UiQueries.EnsureNodesAsync(s.Store);
            return Results.Json(new ProjectDto
            {
                ProjectId = s.Config.ProjectId,
                Root = s.Root,
                SessionCount = await s.Store.CountWorkNodesAsync(),
            }, JsonOptions);
        });

        api.MapGet("/tree", async (string? from, UiState s) =>
        {
            var nodes = await s.Store.ListWorkNodesAsync();
            var labels = await s.Store.ListWorkNodeLabelsAsync();
            var edges = await s.Store.ListWorkNodeEdgesAsync();
            var byNode = labels
                .GroupBy(l => l.WorkNodeId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Label).ToList(), StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(from))
            {
                edges = edges
                    .Where(e => e.FromId == from || e.ToId == from)
                    .ToList();
                var ids = new HashSet<string>(StringComparer.Ordinal) { from };
                foreach (var e in edges)
                {
                    ids.Add(e.FromId);
                    ids.Add(e.ToId);
                }

                nodes = nodes.Where(n => ids.Contains(n.Id)).ToList();
            }

            return Results.Json(new TreeResponseDto
            {
                Nodes = nodes
                    .OrderByDescending(n => n.StartedAt)
                    .Select(n => UiMapper.ToListItem(
                        n,
                        byNode.TryGetValue(n.Id, out var lbs) ? lbs : []))
                    .ToList(),
                Edges = edges.Select(UiMapper.ToEdge).ToList(),
            }, JsonOptions);
        });

        api.MapGet("/search", async (string q, string? type, string? from, UiState s) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.Json(Array.Empty<SearchHitDto>(), JsonOptions);
            }

            string? fromKind = null;
            string? fromRef = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                var parts = from.Split(':', 2);
                if (parts.Length == 2)
                {
                    fromKind = parts[0];
                    fromRef = parts[1];
                }
                else
                {
                    fromKind = "node";
                    fromRef = from;
                }
            }

            var hits = await HistoryQuery.SearchAsync(s.Store, q, type, fromKind, fromRef);
            var dto = hits.Select(h => new SearchHitDto
            {
                WorkNodeId = h.WorkNodeId,
                Snippet = h.Snippet,
                Field = h.Field,
            }).ToList();
            return Results.Json(dto, JsonOptions);
        });

        api.MapGet("/status", async (UiState s) =>
        {
            await UiQueries.EnsureNodesAsync(s.Store);
            var daemonStatus = await UiQueries.ProbeDaemonAsync(s.Config.Bind);
            return Results.Json(new StatusDto
            {
                ProjectId = s.Config.ProjectId,
                Root = s.Root,
                Bind = s.Config.Bind,
                DaemonStatus = daemonStatus,
                DaemonRunning = daemonStatus == "running",
                SessionCount = await s.Store.CountWorkNodesAsync(),
                PromptCount = await s.Store.CountPromptsAsync(),
            }, JsonOptions);
        });

        api.MapGet("/sessions", async (string? file, string? prefix, UiState s) =>
        {
            try
            {
                await s.Store.AttributeCommitsAtHeadAsync();
            }
            catch
            {
                // Best-effort.
            }

            await UiQueries.EnsureNodesAsync(s.Store);

            if (!string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(prefix))
            {
                var touches = await HistoryQuery.FileHistoryAsync(s.Store, file!);
                var touchDto = touches.Select(t => new FileTouchDto
                {
                    SessionId = t.WorkNodeId,
                    Path = t.Path,
                    Action = t.Action,
                    At = t.At,
                    PromptExcerpt = t.PromptExcerpt,
                    CommitShas = t.CommitShas,
                }).ToList();
                return Results.Json(touchDto, JsonOptions);
            }

            var entries = await HistoryQuery.ProjectHistoryAsync(s.Store, file, prefix);
            var sessionsById = (await s.Store.ListSessionsAsync())
                .ToDictionary(x => x.Id, StringComparer.Ordinal);
            var items = entries
                .OrderByDescending(e => e.Node.StartedAt)
                .Select(e =>
                {
                    sessionsById.TryGetValue(e.Node.SessionId, out var sess);
                    return new SessionDto
                    {
                        Id = e.Node.Id,
                        ProjectId = e.Node.ProjectId,
                        Agent = e.Node.Agent,
                        StartedAt = e.Node.StartedAt,
                        EndedAt = e.Node.EndedAt,
                        Status = e.Node.Status,
                        GitHead = sess?.GitHead ?? e.Node.GitHead,
                        GitHeadAfter = sess?.GitHeadAfter ?? e.Node.GitHeadAfter,
                        PromptExcerpt = e.Node.PromptExcerpt,
                        TurnCount = e.Node.TurnCount,
                        FileCount = e.Node.FileCount,
                        CommandCount = e.Node.CommandCount,
                        TestCount = e.Node.TestCount,
                        CommitCount = e.Node.CommitCount,
                        Labels = e.Labels,
                        TopFiles = e.TopFiles,
                        CommitShas = e.CommitShas,
                        IncomingEdges = e.IncomingEdges,
                        OutgoingEdges = e.OutgoingEdges,
                    };
                })
                .ToList();
            return Results.Json(items, JsonOptions);
        });

        api.MapGet("/sessions/{id}", async (string id, UiState s) =>
        {
            var session = await s.Store.GetSessionAsync(id);
            if (session is null)
            {
                return Results.Json(new { error = "session not found" }, statusCode: 404);
            }

            var node = await WorkNodeIndex.ProjectSessionAsync(s.Store, session);
            var detail = await UiMapper.ToDetailAsync(s.Store, node);
            return Results.Json(detail, JsonOptions);
        });

        api.MapGet("/sessions/{id}/events", async (string id, UiState s) =>
        {
            var session = await s.Store.GetSessionAsync(id);
            if (session is null)
            {
                return Results.Json(new { error = "session not found" }, statusCode: 404);
            }

            var events = await s.Store.ListEventsAsync(id);
            return Results.Json(events, EventJson.Options);
        });

        api.MapGet("/sessions/{id}/files/{*path}", async (string id, string path, UiState s) =>
        {
            var session = await s.Store.GetSessionAsync(id);
            if (session is null)
            {
                return Results.Json(new { error = "session not found" }, statusCode: 404);
            }

            var node = await WorkNodeIndex.ProjectSessionAsync(s.Store, session);
            var detail = UiMapper.FindFileDetail(node, path);
            if (detail is null)
            {
                return Results.Json(new { error = "file not found on session" }, statusCode: 404);
            }

            detail = UiMapper.EnrichFileDetail(detail, s.Root, node.GitHead, node.GitHeadAfter);
            return Results.Json(detail, JsonOptions);
        });

        api.MapGet("/sessions/{id}/activity", async (string id, UiState s) =>
        {
            if (await s.Store.GetSessionAsync(id) is null)
            {
                return Results.Json(new { error = "session not found" }, statusCode: 404);
            }

            var timeline = await UiQueries.LoadTimelineAsync(s.Store, id);
            return Results.Json(new ActivityTimelineDto
            {
                SessionId = id,
                Turns = timeline.Turns.Select(UiMapper.ToTurnPublic).ToList(),
            }, JsonOptions);
        });

        api.MapGet("/why/commit/{sha}", async (string sha, UiState s) =>
        {
            var links = await s.Store.ListCommitsByShaAsync(sha);
            if (links.Count == 0)
            {
                return Results.Json(new ProvenanceResultDto
                {
                    SubjectKind = "commit",
                    Sha = sha,
                    Hits = [],
                }, JsonOptions);
            }

            var sessionIds = links.Select(l => l.SessionId).Distinct(StringComparer.Ordinal).ToList();
            var timelines = new List<SessionTimeline>();
            foreach (var sessionId in sessionIds)
            {
                timelines.Add(await UiQueries.LoadTimelineAsync(s.Store, sessionId));
            }

            var result = ProvenanceQuery.Trace(new ProvenanceSubject.Commit(sha), timelines);
            return Results.Json(UiQueries.ToProvenanceDto(result), JsonOptions);
        });

        api.MapGet("/why/file", async (string path, string? lines, UiState s) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.Json(new { error = "path is required" }, statusCode: 400);
            }

            await UiQueries.EnsureNodesAsync(s.Store);

            var candidateIds = await s.Store.ListWorkNodeIdsForFileAsync(path);
            IEnumerable<SessionRecord> sessions;
            if (candidateIds.Count > 0)
            {
                var all = await s.Store.ListSessionsAsync();
                sessions = all.Where(sess => candidateIds.Contains(sess.Id));
            }
            else
            {
                sessions = await s.Store.ListSessionsAsync();
            }

            var timelines = new List<SessionTimeline>();
            foreach (var session in sessions)
            {
                timelines.Add(await UiQueries.LoadTimelineAsync(s.Store, session.Id));
            }

            ProvenanceSubject subject;
            if (!string.IsNullOrWhiteSpace(lines))
            {
                if (!UiQueries.TryParseLineRange(lines, out var start, out var end))
                {
                    return Results.Json(new { error = "invalid lines value; expected start-end (e.g. 10-42)" }, statusCode: 400);
                }

                subject = new ProvenanceSubject.CodeRange(path, start, end);
            }
            else
            {
                subject = new ProvenanceSubject.FilePath(path);
            }

            var result = ProvenanceQuery.Trace(subject, timelines);
            return Results.Json(UiQueries.ToProvenanceDto(result), JsonOptions);
        });

        api.MapPost("/edges", async (LinkEdgeRequest body, UiState s) =>
        {
            if (string.IsNullOrWhiteSpace(body.From) || string.IsNullOrWhiteSpace(body.To))
            {
                return Results.Json(new { error = "from and to are required" }, statusCode: 400);
            }

            await UiQueries.EnsureNodeAsync(s.Store, body.From);
            await UiQueries.EnsureNodeAsync(s.Store, body.To);
            if (await s.Store.GetWorkNodeAsync(body.From) is null)
            {
                return Results.Json(new { error = $"session not found: {body.From}" }, statusCode: 404);
            }

            if (await s.Store.GetWorkNodeAsync(body.To) is null)
            {
                return Results.Json(new { error = $"session not found: {body.To}" }, statusCode: 404);
            }

            var edge = new WorkNodeEdgeRecord
            {
                FromId = body.From,
                ToId = body.To,
                Kind = WorkNodeEdgeKinds.Manual,
                Label = body.Label,
                Score = 1.0,
                Manual = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await s.Store.UpsertManualEdgeAsync(edge);
            return Results.Json(UiMapper.ToEdge(edge), JsonOptions);
        });

        api.MapDelete("/edges", async (string from, string to, string? kind, UiState s) =>
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return Results.Json(new { error = "from and to are required" }, statusCode: 400);
            }

            var edgeKind = string.IsNullOrWhiteSpace(kind) ? WorkNodeEdgeKinds.Manual : kind;
            await s.Store.DeleteEdgeAsync(from, to, edgeKind);
            return Results.Json(new { ok = true, from, to, kind = edgeKind }, JsonOptions);
        });

        api.MapPost("/sessions/{id}/labels", async (string id, LabelRequest body, UiState s) =>
        {
            if (string.IsNullOrWhiteSpace(body.Label))
            {
                return Results.Json(new { error = "label is required" }, statusCode: 400);
            }

            await UiQueries.EnsureNodeAsync(s.Store, id);
            if (await s.Store.GetWorkNodeAsync(id) is null)
            {
                return Results.Json(new { error = $"session not found: {id}" }, statusCode: 404);
            }

            await s.Store.AddWorkNodeLabelAsync(id, body.Label);
            return Results.Json(new { ok = true, id, label = body.Label }, JsonOptions);
        });

        api.MapDelete("/sessions/{id}/labels/{label}", async (string id, string label, UiState s) =>
        {
            await s.Store.RemoveWorkNodeLabelAsync(id, label);
            return Results.Json(new { ok = true, id, label }, JsonOptions);
        });

        api.MapPost("/rebuild", async (UiState s) =>
        {
            var count = await WorkNodeIndex.RebuildAllAsync(s.Store, s.Root);
            return Results.Json(new RebuildResultDto { NodeCount = count }, JsonOptions);
        });
    }

    private static void MapSpa(WebApplication app)
    {
        var provider = EmbeddedUi();
        if (provider is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapFallback(async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "not found" }, JsonOptions);
                    return;
                }

                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(provider.GetFileInfo("index.html"));
            });
        }
        else
        {
            app.MapGet("/", () => Results.Content(
                """
                <!DOCTYPE html>
                <html><head><title>Whyso</title></head>
                <body style="font-family:system-ui;background:#121212;color:#e5e5e5;padding:2rem">
                  <h1>Whyso Explorer</h1>
                  <p>API is running, but this build has no UI embedded. Run <code>npm run build</code> in <code>ui/</code> and rebuild, or run Vite in dev mode.</p>
                  <p>Try <a href="/api/v1/health" style="color:#4ade80">/api/v1/health</a> or <a href="/api/v1/sessions" style="color:#4ade80">/api/v1/sessions</a>.</p>
                </body></html>
                """,
                "text/html"));
        }
    }

    /// <summary>The explorer UI embedded in this assembly, or null when it was built without one.</summary>
    private static IFileProvider? EmbeddedUi()
    {
        try
        {
            var provider = new ManifestEmbeddedFileProvider(typeof(UiHost).Assembly, "wwwroot");
            return provider.GetFileInfo("index.html").Exists ? provider : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
