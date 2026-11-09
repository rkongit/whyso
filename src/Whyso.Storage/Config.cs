using Tomlyn;
using Tomlyn.Model;

namespace Whyso.Storage;

public sealed class Config
{
    public const string DefaultBind = "127.0.0.1:9477";

    public string Bind { get; set; } = DefaultBind;
    public string ProjectId { get; set; } = "";

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
            return new Config { Bind = bind, ProjectId = projectId };
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
        };
        File.WriteAllText(WorktreePaths.ConfigPath(root), Toml.FromModel(table));
    }
}
