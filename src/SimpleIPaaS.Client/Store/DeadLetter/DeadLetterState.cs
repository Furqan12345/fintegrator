using System.Collections.Generic;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class DeadLetterState
{
    public bool IsLoading { get; }
    public IReadOnlyList<DeadLetterDto> DeadLetters { get; }
    public string StatusFilter { get; }

    private DeadLetterState()
    {
        IsLoading = false;
        DeadLetters = new List<DeadLetterDto>();
        StatusFilter = string.Empty;
    }

    public DeadLetterState(bool isLoading, IReadOnlyList<DeadLetterDto> deadLetters, string statusFilter)
    {
        IsLoading = isLoading;
        DeadLetters = deadLetters;
        StatusFilter = statusFilter;
    }
}
