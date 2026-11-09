namespace Whyso.Storage;

public class StorageException : Exception
{
    public StorageException(string message) : base(message) { }
    public StorageException(string message, Exception inner) : base(message, inner) { }

    public static StorageException NotAGitRepository() =>
        new("not a git repository (run from inside a git project)");

    public static StorageException NotInitialized() =>
        new("whyso is not initialized (run `whyso init`)");

    public static StorageException ProjectNotFound() => new("project not found");

    public static StorageException SessionNotFound(string id) =>
        new($"session not found: {id}");

    public static StorageException Config(string message) =>
        new($"config error: {message}");

    public static StorageException Git(string message) =>
        new($"git error: {message}");

    public static StorageException Validation(string message) =>
        new($"validation error: {message}");
}
