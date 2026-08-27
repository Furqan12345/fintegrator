using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SimpleIPaaS.Api.Controllers;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.Execution;

public class ExecutionStepsEndpointTests
{
    private const string ReceivedInputBody = "{\"received\":\"RECEIVED_INPUT_MARKER\"}";
    private const string RequestPayloadBody = "{\"request\":\"REQUEST_PAYLOAD_MARKER\"}";
    private const string ResponsePayloadBody = "{\"response\":\"RESPONSE_PAYLOAD_MARKER\"}";
    private const string PacketRequestBody = "{\"packetRequest\":\"PACKET_REQUEST_MARKER\"}";
    private const string PacketResponseBody = "{\"packetResponse\":\"PACKET_RESPONSE_MARKER\"}";
    private const string PacketRequestHeaders = "{\"Accept\":\"PACKET_REQUEST_HEADER_MARKER\"}";
    private const string PacketResponseHeaders = "{\"Content-Type\":\"PACKET_RESPONSE_HEADER_MARKER\"}";

    private readonly StubExecutionRepository _executions = new();
    private readonly Guid _executionId = Guid.NewGuid();
    private readonly StepExecution _step;
    private readonly StepPacketLog _packet;

    public ExecutionStepsEndpointTests()
    {
        _step = new StepExecution
        {
            FlowExecutionId = _executionId,
            StepId = Guid.NewGuid(),
            NodeName = "FetchOrders",
            Status = ExecutionStatus.Success,
            HttpStatusCode = 200,
            ReceivedInput = ReceivedInputBody,
            RequestPayload = RequestPayloadBody,
            ResponsePayload = ResponsePayloadBody
        };

        _packet = new StepPacketLog
        {
            StepExecutionId = _step.Id,
            Sequence = 1,
            Kind = "Http",
            HttpMethod = "GET",
            RequestUrl = "https://example.test/orders",
            StatusCode = 200,
            RequestHeadersJson = PacketRequestHeaders,
            RequestBody = PacketRequestBody,
            ResponseHeadersJson = PacketResponseHeaders,
            ResponseBody = PacketResponseBody,
            DurationMs = 42
        };

        _executions.StepExecutions.Add(_step);
        _executions.StepPacketLogs.Add(_packet);
    }

    private ExecutionController CreateController() =>
        new(_executions);

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task GetExecutionSteps_DoesNotReturnAnyPayloadBodies()
    {
        var steps = Body<List<StepExecutionDto>>(await CreateController().GetExecutionSteps(_executionId));

        var json = JsonSerializer.Serialize(steps);

        Assert.DoesNotContain("RECEIVED_INPUT_MARKER", json);
        Assert.DoesNotContain("REQUEST_PAYLOAD_MARKER", json);
        Assert.DoesNotContain("RESPONSE_PAYLOAD_MARKER", json);
        Assert.DoesNotContain("PACKET_REQUEST_MARKER", json);
        Assert.DoesNotContain("PACKET_RESPONSE_MARKER", json);
        Assert.DoesNotContain("PACKET_REQUEST_HEADER_MARKER", json);
        Assert.DoesNotContain("PACKET_RESPONSE_HEADER_MARKER", json);
    }

    [Fact]
    public async Task GetExecutionSteps_ReturnsMetadataAndSizes()
    {
        var step = Assert.Single(Body<List<StepExecutionDto>>(await CreateController().GetExecutionSteps(_executionId)));

        Assert.Equal(_step.Id, step.Id);
        Assert.Equal("FetchOrders", step.NodeName);
        Assert.Equal("Success", step.Status);
        Assert.Equal(200, step.HttpStatusCode);
        Assert.Equal(ReceivedInputBody.Length, step.ReceivedInputSize);
        Assert.Equal(RequestPayloadBody.Length, step.RequestPayloadSize);
        Assert.Equal(ResponsePayloadBody.Length, step.ResponsePayloadSize);

        var packet = Assert.Single(step.Packets);
        Assert.Equal(_packet.Id, packet.Id);
        Assert.Equal("GET", packet.HttpMethod);
        Assert.Equal(42, packet.DurationMs);
        Assert.Equal(PacketRequestHeaders.Length, packet.RequestHeadersSize);
        Assert.Equal(PacketRequestBody.Length, packet.RequestBodySize);
        Assert.Equal(PacketResponseHeaders.Length, packet.ResponseHeadersSize);
        Assert.Equal(PacketResponseBody.Length, packet.ResponseBodySize);
    }

    [Fact]
    public async Task GetStepPayload_ReturnsTheFullPayloadsForThatStep()
    {
        var payload = Body<StepPayloadDto>(await CreateController().GetStepPayload(_executionId, _step.Id));

        Assert.Equal(_step.Id, payload.Id);
        Assert.Equal(ReceivedInputBody, payload.ReceivedInput);
        Assert.Equal(RequestPayloadBody, payload.RequestPayload);
        Assert.Equal(ResponsePayloadBody, payload.ResponsePayload);
    }

    [Fact]
    public async Task GetStepPayload_ReturnsNotFoundForAStepFromAnotherExecution()
    {
        Assert.IsType<NotFoundResult>(await CreateController().GetStepPayload(Guid.NewGuid(), _step.Id));
        Assert.IsType<NotFoundResult>(await CreateController().GetStepPayload(_executionId, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetPacketBody_ReturnsTheFullBodiesAndHeaders()
    {
        var body = Body<StepPacketBodyDto>(await CreateController().GetPacketBody(_executionId, _packet.Id));

        Assert.Equal(_packet.Id, body.Id);
        Assert.Equal(PacketRequestHeaders, body.RequestHeadersJson);
        Assert.Equal(PacketRequestBody, body.RequestBody);
        Assert.Equal(PacketResponseHeaders, body.ResponseHeadersJson);
        Assert.Equal(PacketResponseBody, body.ResponseBody);
    }

    [Fact]
    public async Task GetPacketBody_ReturnsNotFoundForAPacketFromAnotherExecution()
    {
        Assert.IsType<NotFoundResult>(await CreateController().GetPacketBody(Guid.NewGuid(), _packet.Id));
        Assert.IsType<NotFoundResult>(await CreateController().GetPacketBody(_executionId, Guid.NewGuid()));
    }
}
