using System;
using System.Threading.Tasks;

namespace SimpleIPaaS.Application.Interfaces;

public class ScriptExecutionException : Exception
{
    public ScriptExecutionException(string message, bool timedOut = false, Exception? innerException = null)
        : base(message, innerException)
    {
        TimedOut = timedOut;
    }

    public bool TimedOut { get; }
}

public interface ICodeExecutionService
{
    Task<string> ExecuteMappingAsync(string csharpCode, string inputJson);
}
