using System;

namespace SimpleIPaaS.Client.Store;

public class ToastMessage
{
    public Guid Id { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = "success";
}

public class ShowToastAction
{
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = "success";
}

public class ToastAddedAction { public ToastMessage Toast { get; set; } = new(); }

public class DismissToastAction { public Guid Id { get; set; } }
