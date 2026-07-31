using System;
using System.Collections.Generic;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadCrossReferenceListsAction { }

public class LoadCrossReferenceListsResultAction
{
    public IEnumerable<CrossReferenceListDto> Lists { get; set; } = Array.Empty<CrossReferenceListDto>();
}

public class CreateCrossReferenceListAction
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class DeleteCrossReferenceListAction
{
    public string Name { get; set; } = string.Empty;
}

public class ClearCrossReferenceListAction
{
    public string Name { get; set; } = string.Empty;
}

public class LoadCrossReferenceEntriesAction
{
    public string ListName { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
}

public class LoadCrossReferenceEntriesResultAction
{
    public string ListName { get; set; } = string.Empty;
    public CrossReferenceEntryPageDto Result { get; set; } = new();
}

public class DeleteCrossReferenceEntryAction
{
    public Guid Id { get; set; }
    public string ListName { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
}

public class CloseCrossReferenceListAction { }
