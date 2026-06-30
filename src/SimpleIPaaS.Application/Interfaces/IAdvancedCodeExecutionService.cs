using System.Threading.Tasks;

namespace SimpleIPaaS.Application.Interfaces;

public interface IAdvancedCodeExecutionService
{
    Task<string> ExecuteMappingAsync(string csharpCode, string flowStateJson);
    Task<string> ExecutePostFlightAsync(string csharpCode, string flowStateJson, string httpResponseJson);
    Task<string> ExecuteUrlAsync(string csharpCode, string flowStateJson);
    Task<bool> ExecuteBranchAsync(string csharpCode, string flowStateJson);
}
