using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Api;

// The connections wizard used to save a completely blank connection (name='', baseUrl='') and
// the API accepted it. These tests pin the server-side rules the controller now enforces, so a
// blank or unusable connection can never reach the database again.
public sealed class ConnectionControllerValidationTests
{
    // Mirrors the running host: AddControllers() registers the MVC ProblemDetailsFactory so the
    // produced 400 carries Status=400 and the application/problem+json shape.
    private static ConnectionController CreateController(StubConnectionRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        return new ConnectionController(repository, new PassthroughEncryptionService(), new StubTransportEngine())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() }
            }
        };
    }

    private static ValidationProblemDetails AssertValidationProblem(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        return Assert.IsType<ValidationProblemDetails>(objectResult.Value);
    }

    private static string AllErrors(ValidationProblemDetails problem) =>
        string.Join(" ", problem.Errors.SelectMany(pair => pair.Value));

    [Fact]
    public async Task Create_WithEmptyForm_ReturnsValidationProblemAndWritesNothing()
    {
        var repository = new StubConnectionRepository();
        var controller = CreateController(repository);

        var result = await controller.Create(new ConnectionDto { Name = "", BaseUrl = "", AuthType = "None", Status = "Active" });

        var errors = AllErrors(AssertValidationProblem(result));
        Assert.Contains("name is required", errors, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("base URL is required", errors, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Theory]
    [InlineData("api.example.com")]                 // not absolute
    [InlineData("ftp://api.example.com")]           // wrong scheme
    [InlineData("not a url")]
    public async Task Create_WithNonHttpBaseUrl_IsRejected(string baseUrl)
    {
        var controller = CreateController(new StubConnectionRepository());

        var result = await controller.Create(new ConnectionDto { Name = "Salesforce", BaseUrl = baseUrl, AuthType = "None", Status = "Active" });

        Assert.Contains("absolute http(s) URL", AllErrors(AssertValidationProblem(result)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithAuthTypeButNoCredentials_IsRejected()
    {
        var controller = CreateController(new StubConnectionRepository());

        var result = await controller.Create(new ConnectionDto
        {
            Name = "Business Central",
            BaseUrl = "https://api.businesscentral.dynamics.com/v2.0",
            AuthType = "Basic",
            AuthConfigJson = "",
            Status = "Active"
        });

        Assert.Contains("Credentials are required", AllErrors(AssertValidationProblem(result)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithIncompleteAuthConfig_NamesTheMissingFields()
    {
        var controller = CreateController(new StubConnectionRepository());

        var result = await controller.Create(new ConnectionDto
        {
            Name = "Business Central",
            BaseUrl = "https://api.businesscentral.dynamics.com/v2.0",
            AuthType = "Basic",
            AuthConfigJson = "{\"username\":\"svc-account\"}",
            Status = "Active"
        });

        Assert.Contains("password", AllErrors(AssertValidationProblem(result)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithNameAndCredentials_Succeeds()
    {
        var repository = new StubConnectionRepository();
        var controller = CreateController(repository);

        var result = await controller.Create(new ConnectionDto
        {
            Name = "  Business Central  ",
            BaseUrl = " https://api.businesscentral.dynamics.com/v2.0 ",
            AuthType = "Basic",
            AuthConfigJson = "{\"username\":\"svc-account\",\"password\":\"secret\"}",
            Status = "Active"
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var dto = Assert.IsType<ConnectionDto>(created.Value);
        Assert.Equal("Business Central", dto.Name);
        Assert.Equal("https://api.businesscentral.dynamics.com/v2.0", dto.BaseUrl);
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public async Task Create_WithShopifyClientCredentials_Succeeds()
    {
        var repository = new StubConnectionRepository();
        var controller = CreateController(repository);

        var result = await controller.Create(new ConnectionDto
        {
            Name = "Shopify client",
            BaseUrl = "https://fintegrator.myshopify.com",
            AuthType = "Shopify",
            AuthConfigJson = "{\"shop\":\"fintegrator.myshopify.com\",\"clientId\":\"cid\",\"clientSecret\":\"secret\",\"apiVersion\":\"2026-10\"}",
            Status = "Active"
        });

        Assert.IsType<CreatedAtActionResult>(result);
        Assert.Single(await repository.GetAllAsync());
    }

    [Fact]
    public async Task Create_WithInvalidShopifyShopDomain_IsRejected()
    {
        var controller = CreateController(new StubConnectionRepository());

        var result = await controller.Create(new ConnectionDto
        {
            Name = "Shopify client",
            BaseUrl = "https://fintegrator.myshopify.com",
            AuthType = "Shopify",
            AuthConfigJson = "{\"shop\":\"https://example.com\",\"clientId\":\"cid\",\"clientSecret\":\"secret\",\"apiVersion\":\"2026-10\"}",
            Status = "Active"
        });

        Assert.Contains("valid your-store.myshopify.com", AllErrors(AssertValidationProblem(result)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_KeepingStoredCredentials_StaysAllowed()
    {
        var repository = new StubConnectionRepository();
        var existing = new SimpleIPaaS.Domain.Entities.Connection
        {
            Id = Guid.NewGuid(),
            Name = "Business Central",
            BaseUrl = "https://api.businesscentral.dynamics.com/v2.0",
            AuthType = SimpleIPaaS.Domain.AuthType.Basic,
            AuthConfigJson = "encrypted-blob",
            Status = SimpleIPaaS.Domain.ConnectionStatus.Active
        };
        repository.Seed(existing);
        var controller = CreateController(repository);

        var result = await controller.Update(existing.Id, new ConnectionDto
        {
            Id = existing.Id,
            Name = "Business Central (prod)",
            BaseUrl = existing.BaseUrl,
            AuthType = "Basic",
            AuthConfigJson = "",
            Status = "Active"
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Business Central (prod)", (await repository.GetByIdAsync(existing.Id))!.Name);
    }

    [Fact]
    public async Task Update_ChangingAuthTypeWithoutNewCredentials_IsRejected()
    {
        var repository = new StubConnectionRepository();
        var existing = new SimpleIPaaS.Domain.Entities.Connection
        {
            Id = Guid.NewGuid(),
            Name = "Business Central",
            BaseUrl = "https://api.businesscentral.dynamics.com/v2.0",
            AuthType = SimpleIPaaS.Domain.AuthType.Basic,
            AuthConfigJson = "encrypted-blob",
            Status = SimpleIPaaS.Domain.ConnectionStatus.Active
        };
        repository.Seed(existing);
        var controller = CreateController(repository);

        var result = await controller.Update(existing.Id, new ConnectionDto
        {
            Id = existing.Id,
            Name = existing.Name,
            BaseUrl = existing.BaseUrl,
            AuthType = "Bearer",
            AuthConfigJson = "",
            Status = "Active"
        });

        Assert.Contains("requires entering the credentials", AllErrors(AssertValidationProblem(result)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SimpleIPaaS.Domain.AuthType.Basic, (await repository.GetByIdAsync(existing.Id))!.AuthType);
    }

    [Fact]
    public async Task Update_WithBlankFields_IsRejected()
    {
        var repository = new StubConnectionRepository();
        var existing = new SimpleIPaaS.Domain.Entities.Connection
        {
            Id = Guid.NewGuid(),
            Name = "Shopify",
            BaseUrl = "https://shop.myshopify.com",
            AuthType = SimpleIPaaS.Domain.AuthType.None,
            AuthConfigJson = "",
            Status = SimpleIPaaS.Domain.ConnectionStatus.Active
        };
        repository.Seed(existing);
        var controller = CreateController(repository);

        var result = await controller.Update(existing.Id, new ConnectionDto { Id = existing.Id, Name = "", BaseUrl = "", AuthType = "None", Status = "Active" });

        var errors = AllErrors(AssertValidationProblem(result));
        Assert.Contains("name is required", errors, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Shopify", (await repository.GetByIdAsync(existing.Id))!.Name);
    }
}
