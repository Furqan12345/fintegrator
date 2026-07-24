using System;
using System.Collections.Generic;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadDeadLettersAction { public string Status { get; set; } = string.Empty; }
public class LoadDeadLettersResultAction { public IEnumerable<DeadLetterDto> DeadLetters { get; set; } = Array.Empty<DeadLetterDto>(); }

public class RetryDeadLetterAction
{
    public Guid Id { get; set; }
    public string StatusFilter { get; set; } = string.Empty;
}

public class DiscardDeadLetterAction
{
    public Guid Id { get; set; }
    public string StatusFilter { get; set; } = string.Empty;
}
