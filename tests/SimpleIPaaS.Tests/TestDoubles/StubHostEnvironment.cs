using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace SimpleIPaaS.Tests.TestDoubles;

public sealed class StubHostEnvironment : IHostEnvironment
{
    public StubHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "SimpleIPaaS.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
