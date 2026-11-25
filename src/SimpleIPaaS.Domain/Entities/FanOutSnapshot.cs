using System;

namespace SimpleIPaaS.Domain.Entities;

// The flow state a Fan Out card saw, saved once per Fan Out instead of once per child run. A child run starts
// from this state plus its own item (FlowExecution.FanOutItemJson), so 1,000 children of a 2 MB state cost one
// 2 MB row, not 2 GB. A failed child's dead letter stores only its difference from it. Deleted with the parent run,
// which retention keeps while any child run still exists.
public class FanOutSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParentExecutionId { get; set; }
    public Guid FanOutNodeId { get; set; }

    // Where the item goes in each child's flow state: flowState[ItemVariable] and, when the card has a name,
    // flowState[NodeName].
    public string ItemVariable { get; set; } = "item";
    public string NodeName { get; set; } = string.Empty;

    public string FlowStateJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
