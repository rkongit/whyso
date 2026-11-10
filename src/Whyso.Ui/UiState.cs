using Whyso.Storage;

namespace Whyso.Ui;

public sealed class UiState
{
    public required Store Store { get; init; }
    public required Config Config { get; init; }
    public required string Root { get; init; }
}
