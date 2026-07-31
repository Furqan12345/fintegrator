using System.Collections.Generic;
using System.Linq;
using Fluxor;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public static class CrossReferenceReducers
{
    [ReducerMethod]
    public static CrossReferenceState ReduceLoadCrossReferenceListsAction(CrossReferenceState state, LoadCrossReferenceListsAction action) =>
        Loading(state);

    [ReducerMethod]
    public static CrossReferenceState ReduceLoadCrossReferenceListsResultAction(CrossReferenceState state, LoadCrossReferenceListsResultAction action) =>
        new(false, action.Lists.ToList(), state.SelectedList, state.Entries, state.TotalCount, state.Page, state.PageSize, state.Search);

    [ReducerMethod]
    public static CrossReferenceState ReduceCreateCrossReferenceListAction(CrossReferenceState state, CreateCrossReferenceListAction action) =>
        Loading(state);

    [ReducerMethod]
    public static CrossReferenceState ReduceDeleteCrossReferenceListAction(CrossReferenceState state, DeleteCrossReferenceListAction action) =>
        Loading(state);

    [ReducerMethod]
    public static CrossReferenceState ReduceClearCrossReferenceListAction(CrossReferenceState state, ClearCrossReferenceListAction action) =>
        Loading(state);

    [ReducerMethod]
    public static CrossReferenceState ReduceLoadCrossReferenceEntriesAction(CrossReferenceState state, LoadCrossReferenceEntriesAction action) =>
        new(true, state.Lists, action.ListName, state.Entries, state.TotalCount, action.Page, state.PageSize, action.Search);

    [ReducerMethod]
    public static CrossReferenceState ReduceLoadCrossReferenceEntriesResultAction(CrossReferenceState state, LoadCrossReferenceEntriesResultAction action) =>
        new(
            false,
            state.Lists,
            action.ListName,
            action.Result.Entries,
            action.Result.TotalCount,
            action.Result.Page,
            action.Result.PageSize,
            state.Search);

    [ReducerMethod]
    public static CrossReferenceState ReduceDeleteCrossReferenceEntryAction(CrossReferenceState state, DeleteCrossReferenceEntryAction action) =>
        Loading(state);

    [ReducerMethod]
    public static CrossReferenceState ReduceCloseCrossReferenceListAction(CrossReferenceState state, CloseCrossReferenceListAction action) =>
        new(state.IsLoading, state.Lists, string.Empty, new List<CrossReferenceEntryDto>(), 0, 1, state.PageSize, string.Empty);

    private static CrossReferenceState Loading(CrossReferenceState state) =>
        new(true, state.Lists, state.SelectedList, state.Entries, state.TotalCount, state.Page, state.PageSize, state.Search);
}
