using Tomlyn;
using Tomlyn.Model;

namespace Whyso.Storage;

public sealed class Config
{
    public const string DefaultBind = "127.0.0.1:9477";

    public string Bind { get; set; } = DefaultBind;
    public string ProjectId { get; set; } = "";

    /// <summary>Inject the <c>whyso context</c> brief when a Claude Code session starts. Default on.</summary>
    public bool ContextOnSessionStart { get; set; } = true;

    /// <summary>Write provenance to <c>refs/notes/whyso</c> for attributed commits. Opt-in, default off.</summary>
    public bool GitNotes { get; set; }

    /// <summary>Settable keys for <c>whyso config</c>, with their TOML names.</summary>
    public static readonly IReadOnlyDictionary<string, string> BoolKeys = new Dictionary<string, string>
    {
        ["context.session_start"] = "context_on_session_start",
        ["git.notes"] = "git_notes",
    };

    public static Config New(string projectId) => new()
    {
        Bind = DefaultBind,
        ProjectId = projectId,
    };

    public static Config Load(string root)
    {
        var path = WorktreePaths.ConfigPath(root);
        if (!File.Exists(path))
        {
            throw StorageException.NotInitialized();
        }

        try
        {
            var model = Toml.ToModel(File.ReadAllText(path));
            var bind = model.TryGetValue("bind", out var bindNode) && bindNode is string b
                ? b
                : DefaultBind;
            var projectId = model.TryGetValue("project_id", out var idNode) && idNode is string id
                ? id
                : throw StorageException.Config("missing project_id");
            return new Config
            {
                Bind = bind,
                ProjectId = projectId,
                ContextOnSessionStart = ReadBool(model, "context_on_session_start", true),
                GitNotes = ReadBool(model, "git_notes", false),
            };
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw StorageException.Config(ex.Message);
        }
    }

    public static void Save(string root, Config config)
    {
        var table = new TomlTable
        {
            ["bind"] = config.Bind,
            ["project_id"] = config.ProjectId,
            ["context_on_session_start"] = config.ContextOnSessionStart,
            ["git_notes"] = config.GitNotes,
        };
        File.WriteAllText(WorktreePaths.ConfigPath(root), Toml.FromModel(table));
    }

    /// <summary>Sets a key from <see cref="BoolKeys"/>. Returns false for an unknown key.</summary>
    public bool TrySet(string key, bool value)
    {
        switch (key)
        {
            case "context.session_start":
                ContextOnSessionStart = value;
                return true;
            case "git.notes":
                GitNotes = value;
                return true;
            default:
                return false;
        }
    }

    private static bool ReadBool(TomlTable model, string key, bool fallback) =>
        model.TryGetValue(key, out var node) && node is bool value ? value : fallback;
}
