using System.Threading;
using System.Threading.Tasks;

namespace SimpleIPaaS.Application.Interfaces;

public interface IAdvancedCodeExecutionService
{
    // helperCode is the flow-level helper script. It is prepended to the node script so both
    // share one scope; an empty helper leaves the node script untouched.
    Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null);

    // Email scripts return an object ({ to, cc, subject, body, isHtml }) rather than a string;
    // the implementation serializes it to JSON for the caller.
    Task<string> ExecuteEmailAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null);
    Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string persistedStateJson, string httpResponseJson, string? helperCode = null);
    Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null);
    Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson, string persistedStateJson, string? helperCode = null);

    // Compiles helper code without executing it, so a typo is rejected before it can break
    // every script in the flow.
    Task ValidateAsync(string csharpCode, CancellationToken cancellationToken = default);
}
