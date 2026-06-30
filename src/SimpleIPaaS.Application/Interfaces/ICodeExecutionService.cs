using System.Threading.Tasks;

namespace SimpleIPaaS.Application.Interfaces;

public interface ICodeExecutionService
{
    Task<string> ExecuteMappingAsync(string csharpCode, string inputJson);
}
