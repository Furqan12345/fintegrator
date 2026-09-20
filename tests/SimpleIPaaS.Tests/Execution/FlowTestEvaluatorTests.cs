using System;
using System.Collections.Generic;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using Xunit;

namespace SimpleIPaaS.Tests.Execution;

public sealed class FlowTestEvaluatorTests
{
    [Fact]
    public void EqualsJsonPayloadIgnoresFormattingDifferences()
    {
        var execution = new FlowExecution { Status = ExecutionStatus.Success };
        var steps = new[]
        {
            new StepExecution
            {
                NodeName = "FilterNewSKUs",
                Status = ExecutionStatus.Success,
                ResponsePayload = "{\"orders\":[{\"id\":\"ORDER-100\",\"items\":[{\"sku\":\"SKU-001\"}]}]}"
            }
        };
        var definition = new FlowTestDefinition
        {
            Assertions = new List<FlowTestAssertion>
            {
                new()
                {
                    Type = "Node",
                    NodeName = "FilterNewSKUs",
                    Field = "ResponsePayload",
                    Operator = "Equals",
                    ExpectedValue = "{\n  \"orders\": [\n    { \"id\": \"ORDER-100\", \"items\": [{ \"sku\": \"SKU-001\" }] }\n  ]\n}"
                }
            }
        };

        var result = FlowTestEvaluator.Evaluate(execution, steps, definition);

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }
}