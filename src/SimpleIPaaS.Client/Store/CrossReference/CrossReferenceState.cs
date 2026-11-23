using System.Collections.Generic;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

[FeatureState]
public class CrossReferenceState
{
    public bool IsLoading { get; }
    public IReadOnlyList<CrossReferenceListDto> Lists { get; }
    public string SelectedList { get; }
    public IReadOnlyList<CrossReferenceEntryDto> Entries { get; }
    public int TotalCount { get; }
    public int Page { get; }
    public int PageSize { get; }
    public string Search { get; }
    /// <summary>The last list load failed; the page shows a retry state instead of "no lists".</summary>
    public bool LoadFailed { get; }

    private CrossReferenceState()
    {
        IsLoading = false;
        Lists = new List<CrossReferenceListDto>();
        SelectedList = string.Empty;
        Entries = new List<CrossReferenceEntryDto>();
        TotalCount = 0;
        Page = 1;
        PageSize = 50;
        Search = string.Empty;
    }

    public CrossReferenceState(
        bool isLoading,
        IReadOnlyList<CrossReferenceListDto> lists,
        string selectedList,
        IReadOnlyList<CrossReferenceEntryDto> entries,
        int totalCount,
        int page,
        int pageSize,
        string search,
        bool loadFailed = false)
    {
        LoadFailed = loadFailed;
        IsLoading = isLoading;
        Lists = lists;
        SelectedList = selectedList;
        Entries = entries;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
        Search = search;
    }
}
