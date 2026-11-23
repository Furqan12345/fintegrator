using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Api.Mappings;
using SimpleIPaaS.Api.Validation;
using SimpleIPaaS.Application.Interfaces;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Application.Services;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Infrastructure.Services;
using SimpleIPaaS.Shared.Models;
using SimpleIPaaS.Tests.TestDoubles;

namespace SimpleIPaaS.Tests.ShopifyBusinessCentral;

/// <summary>
/// Guards the shipped Shopify &lt;-&gt; Business Central integration (tools/FintegratorShopifyBc).
/// Two layers:
///  1. the generated flows must stay graph-valid, lint-clean, port-correct and script-compilable;
///  2. the engine, running those exact flows, must produce the documented vendor payloads -
///     the Business Central sales order contract, the updatedAt watermark advance across two runs,
///     the Shopify fulfillment with the BC tracking number and the absolute inventory set.
/// </summary>
public class ShopifyBusinessCentralFlowTests
{
    private const string ShopifyConnectionId = "11111111-1111-1111-1111-111111111111";
    private const string BcConnectionId = "22222222-2222-2222-2222-222222222222";
    private const string IntegrationId = "33333333-3333-3333-3333-333333333333";
    private const string BcCompanyId = "44444444-4444-4444-4444-444444444444";

    private const string OrderCreateFlow = "order-create.json";
    private const string OrderShipmentFlow = "order-shipment.json";
    private const string InventoryFlow = "inventory-update.json";

    private readonly StubIntegrationRepository _flows = new();
    private readonly StubExecutionRepository _executions = new();
    private readonly StubCrossReferenceRepository _crossReferences = new();
    private readonly RecordingTransportEngine _transport = new();

    // ------------------------------------------------------------------ fixtures

    private static AdvancedCodeExecutionService CreateCodeService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scripting:TimeoutSeconds"] = "60" })
            .Build();

        return new AdvancedCodeExecutionService(configuration);
    }

    private static string ResolveToolRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SimpleIPaaS.slnx")))
            {
                return Path.Combine(directory.FullName, "tools", "FintegratorShopifyBc");
            }

            directory = directory.Parent;
        }

        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "FintegratorShopifyBc"));
        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new InvalidOperationException("Could not locate tools/FintegratorShopifyBc from " + AppContext.BaseDirectory);
    }

    private static string ReadBuiltFlowJson(string fileName) =>
        File.ReadAllText(Path.Combine(ResolveToolRoot(), "flows", "build", fileName));

    /// <summary>Loads a generated flow and swaps the deploy-time connection placeholders for test ids.</summary>
    private static IntegrationFlowDto LoadBuiltFlow(string fileName)
    {
        var json = ReadBuiltFlowJson(fileName)
            .Replace("__SHOPIFY_CONNECTION__", ShopifyConnectionId)
            .Replace("__BC_CONNECTION__", BcConnectionId)
            .Replace("__INTEGRATION_ID__", IntegrationId);

        return JsonSerializer.Deserialize<IntegrationFlowDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException(fileName + " failed to deserialize.");
    }

    private FlowExecutor CreateExecutor() =>
        new(_flows, _transport, CreateCodeService(), _executions, _crossReferences, new FakeEmailSender(), NullLogger<FlowExecutor>.Instance);

    private IntegrationFlow SeedFlow(IntegrationFlowDto dto, string persistedStateJson)
    {
        var flow = dto.ToEntity();
        flow.Id = Guid.NewGuid();
        flow.PersistedStateJson = persistedStateJson;
        _flows.Seed(flow);
        return flow;
    }

    private FlowExecution SeedExecution(IntegrationFlow flow)
    {
        var execution = new FlowExecution { FlowId = flow.Id, TriggerSource = "Test" };
        _executions.Seed(execution);
        return execution;
    }

    private static string ShopifyOrderItem(string gid, string name, string updatedAt, decimal quantity) =>
        new JObject
        {
            ["id"] = gid,
            ["name"] = name,
            ["updatedAt"] = updatedAt,
            ["createdAt"] = "2026-10-14T18:00:00Z",
            ["currencyCode"] = "USD",
            ["lineItems"] = new JObject
            {
                ["edges"] = new JArray
                {
                    new JObject
                    {
                        ["node"] = new JObject
                        {
                            ["name"] = "T-Shirt Black M",
                            ["sku"] = "TSHIRT-BLK-M",
                            ["quantity"] = quantity,
                            ["originalUnitPriceSet"] = new JObject { ["shopMoney"] = new JObject { ["amount"] = "29.95" } }
                        }
                    }
                }
            },
            ["shippingAddress"] = new JObject { ["name"] = "Jane Doe", ["address1"] = "1 Main Street", ["city"] = "Springfield", ["zip"] = "62701", ["countryCodeV2"] = "US" },
            ["currentTotalPriceSet"] = new JObject { ["shopMoney"] = new JObject { ["amount"] = "129.90" } }
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string BcShipmentItem() =>
        new JObject
        {
            ["id"] = "66666666-6666-6666-6666-666666666666",
            ["entryNo"] = 11,
            ["salesShipmentNo"] = "102301",
            ["salesOrderNo"] = "101001",
            ["shopifyOrderId"] = "gid://shopify/Order/5100001001",
            ["shopifyOrderName"] = "#1001",
            ["trackingNo"] = "1Z999AA10123456784",
            ["trackingUrl"] = "https://www.ups.com/track?tracknum=1Z999AA10123456784",
            ["shippingAgentCode"] = "UPS",
            ["shippingAgentName"] = "UPS",
            ["shippedQuantity"] = 2,
            ["fullyShipped"] = true,
            ["shopifyPushed"] = false,
            ["fintegratorShippedOrderLines"] = new JArray
            {
                new JObject { ["lineNo"] = 10000, ["itemNo"] = "TSHIRT-BLK-M", ["sku"] = "TSHIRT-BLK-M", ["quantity"] = 2 }
            }
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string InventoryChangeItem(string sku, decimal inventory) =>
        new JObject
        {
            ["entryNo"] = 21,
            ["itemNo"] = sku,
            ["variantCode"] = "",
            ["locationCode"] = "WH1",
            ["sku"] = sku,
            ["inventory"] = inventory,
            ["changeQuantity"] = -3
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string PersistedState(string? extraKey = null, object? extraValue = null, string apiVersion = "2026-07")
    {
        var state = new JObject
        {
            ["bcCompanyId"] = BcCompanyId,
            ["shopifyApiVersion"] = apiVersion
        };

        if (extraKey != null)
        {
            state[extraKey] = extraValue == null ? JValue.CreateNull() : JToken.FromObject(extraValue);
        }

        return state.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string PersistedStateWith(JObject extras, string apiVersion = "2026-07")
    {
        var state = new JObject
        {
            ["bcCompanyId"] = BcCompanyId,
            ["shopifyApiVersion"] = apiVersion
        };

        foreach (var property in extras.Properties())
        {
            state[property.Name] = property.Value.DeepClone();
        }

        return state.ToString(Newtonsoft.Json.Formatting.None);
    }

    private async Task<FlowExecution> RunFromNodesAsync(IntegrationFlow flow, FlowExecution execution, IReadOnlyCollection<Guid> startNodeIds, string flowState, string persistedState)
    {
        var executor = CreateExecutor();
        return await executor.ExecuteFlowFromNodesAsync(flow.Id, execution.Id, startNodeIds, flowState, persistedState, CancellationToken.None);
    }

    private async Task<List<FlowExecution>> ChildrenOfAsync(FlowExecution parent)
    {
        var all = await _executions.GetFlowExecutionsAsync(new ExecutionQuery());
        return all.Where(item => item.ParentExecutionId == parent.Id).ToList();
    }

    /// <summary>
    /// Reads persisted state the way the engine stores it, without Json.NET's automatic
    /// ISO-string-to-DateTime conversion (which is exactly the trap FinValues exists for).
    /// </summary>
    private static JObject ParseStateRaw(string json)
    {
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
        return JObject.Load(reader);
    }

    // ------------------------------------------------------------------ 1. authoring gates

    [Theory]
    [InlineData(OrderCreateFlow)]
    [InlineData(OrderShipmentFlow)]
    [InlineData(InventoryFlow)]
    public void Generated_flow_passes_validation_lint_ports_and_script_compilation(string fileName)
    {
        var flow = LoadBuiltFlow(fileName);
        var findings = new List<string>();
        findings.AddRange(FlowValidator.Validate(flow));
        var (lintErrors, lintWarnings) = StepConfigLinter.Lint(flow);
        findings.AddRange(lintErrors);
        findings.AddRange(lintWarnings);
        findings.AddRange(FlowDraftChecks.ValidateCanvasEdges(flow));
        findings.AddRange(FlowDraftChecks.CheckGeneratedScripts(flow));

        Assert.True(findings.Count == 0, fileName + " -> " + string.Join(" | ", findings));
    }

    [Theory]
    [InlineData(OrderCreateFlow)]
    [InlineData(OrderShipmentFlow)]
    [InlineData(InventoryFlow)]
    public void Generated_flow_keeps_deploy_placeholders_and_cron_contract(string fileName)
    {
        var raw = ReadBuiltFlowJson(fileName);
        Assert.Contains("__SHOPIFY_CONNECTION__", raw);
        Assert.Contains("__BC_CONNECTION__", raw);
        Assert.DoesNotContain("$script", raw);

        var flow = LoadBuiltFlow(fileName);
        Assert.Equal("Cron", flow.TriggerType);

        var schedule = Assert.Single(flow.Nodes, node => node.StepType == "Schedule");
        var cron = JObject.Parse(schedule.StepConfig)["schedule"]!["cronExpression"]!.ToString();
        Assert.Equal(flow.CronExpression, cron);
        Assert.Equal(5, cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData(OrderCreateFlow)]
    [InlineData(OrderShipmentFlow)]
    [InlineData(InventoryFlow)]
    public void Fan_out_node_is_configured_and_never_runs_downstream_of_durable_state(string fileName)
    {
        var flow = LoadBuiltFlow(fileName);
        var fanOut = Assert.Single(flow.Nodes, node => node.StepType == "FanOut");
        var config = JObject.Parse(fanOut.StepConfig)["fanOut"]!;
        Assert.Contains("flowState.", config["arrayPath"]!.ToString());
        Assert.False(string.IsNullOrWhiteSpace(config["itemVariable"]!.ToString()));
        Assert.True(config["maxItems"]!.Value<int>() > 0);

        // Fan-out children inherit a snapshot of the persisted state, so a durable-state node
        // reachable from a fan-out would be last-write-wins across children. Our watermark nodes
        // must therefore sit on the parent path, before the fan-out.
        var reachable = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var edge in flow.Edges.Where(edge => edge.SourceNodeId == fanOut.Id))
        {
            queue.Enqueue(edge.TargetNodeId);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!reachable.Add(current))
            {
                continue;
            }

            foreach (var edge in flow.Edges.Where(edge => edge.SourceNodeId == current))
            {
                queue.Enqueue(edge.TargetNodeId);
            }
        }

        var downstreamStateWriters = flow.Nodes
            .Where(node => reachable.Contains(node.Id) && node.StepType == "PersistedState")
            .Select(node => node.NodeName)
            .ToList();

        Assert.True(downstreamStateWriters.Count == 0, "PersistedState reached from the fan-out: " + string.Join(", ", downstreamStateWriters));
    }

    [Fact]
    public void Order_pull_pagination_injects_the_cursor_into_the_graphql_body()
    {
        // Regression guard for the documented "repeated the same continuation value" trap:
        // a GraphQL cursor has to be written into the request body, not the query string.
        var flow = LoadBuiltFlow(OrderCreateFlow);
        var fetch = Assert.Single(flow.Nodes, node => node.NodeName == "FetchShopifyOrders");
        var pagination = JObject.Parse(fetch.StepConfig)["pagination"]!;

        Assert.Equal("Cursor", pagination["style"]!.ToString());
        Assert.Equal("variables.after", pagination["cursorRequestParameter"]!.ToString());
        Assert.Equal("data.orders.pageInfo.endCursor", pagination["cursorResponsePath"]!.ToString());
        Assert.Equal("data.orders.pageInfo.hasNextPage", pagination["hasMoreResponsePath"]!.ToString());
        Assert.Equal("data.orders.edges", pagination["aggregatePath"]!.ToString());
        Assert.Equal("POST", fetch.HttpMethod);
    }

    // ------------------------------------------------------------------ 2. engine behaviour

    [Fact]
    public async Task Order_pull_watermark_survives_a_non_invariant_server_culture()
    {
        // Json.NET parses ISO-8601 looking state values into DateTime tokens while reading state,
        // and DateTime.ToString() follows the server culture: under de-DE a stored watermark would
        // become "15.10.2026 08:00:00" and every Shopify/BC filter would silently stop matching.
        // The generated scripts must keep the wire format stable regardless of the host culture.
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var preflight = dto.Nodes.Single(node => node.NodeName == "FetchShopifyOrders").PreFlightCode;
        var service = CreateCodeService();

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        string body;
        try
        {
            var german = new CultureInfo("de-DE");
            CultureInfo.DefaultThreadCurrentCulture = german;
            CultureInfo.DefaultThreadCurrentUICulture = german;
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;

            body = await service.ExecuteMappingAsync(preflight, "{}", PersistedState("ordersWatermark", "2026-10-15T08:00:00Z"));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUiCulture;
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        var search = JObject.Parse(body)["variables"]!["search"]!.ToString();
        // Watermark rewound by the default overlap (900s) and constrained to paid orders.
        Assert.Equal("updated_at:>'2026-10-15T07:45:00Z' financial_status:paid", search);
    }

    [Fact]
    public async Task Order_create_parent_run_fans_out_and_advances_the_updatedAt_watermark()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState("ordersWatermark", "2026-10-15T08:00:00Z"));
        var execution = SeedExecution(flow);

        _transport.Enqueue(200, """
        { "data": { "orders": { "pageInfo": { "hasNextPage": false, "endCursor": "c1" }, "edges": [
            { "node": { "id": "gid://shopify/Order/5100001001", "name": "#1001", "updatedAt": "2026-10-15T09:10:00Z" } },
            { "node": { "id": "gid://shopify/Order/5100001002", "name": "#1002", "updatedAt": "2026-10-15T09:30:00Z" } }
        ] } } }
        """);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("admin/api/2026-07/graphql.json", _transport.RequestedUrls[0]);
        Assert.Equal(Guid.Parse(ShopifyConnectionId), _transport.ConnectionIds[0]);

        var request = JObject.Parse(_transport.RequestPayloads[0]);
        // The stored watermark is rewound by the default overlap (900s = one cron interval) so an
        // order updated in the same second as the previous batch's newest order cannot be skipped,
        // and only paid orders are pulled.
        Assert.Equal("updated_at:>'2026-10-15T07:45:00Z' financial_status:paid", request["variables"]!["search"]!.ToString());
        Assert.Contains("sortKey: UPDATED_AT", request["query"]!.ToString());

        // One child transaction per order, each starting at the Business Central call.
        var children = await ChildrenOfAsync(execution);
        Assert.Equal(2, children.Count);
        Assert.All(children, child =>
        {
            Assert.Equal(ExecutionStatus.Queued, child.Status);
            Assert.Equal("FanOut", child.TriggerSource);
            Assert.Contains("gid://shopify/Order/510000100", child.InitialFlowStateJson);
        });
        Assert.Equal(new[] { "gid://shopify/Order/5100001001", "gid://shopify/Order/5100001002" }, children
            .OrderBy(child => child.FanOutItemIndex)
            .Select(child => JObject.Parse(child.FanOutItemJson!)["id"]!.ToString())
            .ToArray());

        // The watermark written by the PersistedState card is the newest updatedAt of this run.
        var persisted = ParseStateRaw(flow.PersistedStateJson);
        Assert.Equal("2026-10-15T09:30:00Z", persisted["ordersWatermark"]!.ToString());
        Assert.Equal(BcCompanyId, persisted["bcCompanyId"]!.ToString());
        Assert.Equal(2, persisted["ordersLastCount"]!.Value<int>());

        // Second run: the same flow must ask for orders newer than the stored watermark only.
        var secondExecution = SeedExecution(flow);
        _transport.Enqueue(200, """
        { "data": { "orders": { "pageInfo": { "hasNextPage": false, "endCursor": "c2" }, "edges": [] } } }
        """);

        var secondResult = await CreateExecutor().ExecuteFlowAsync(flow.Id, secondExecution.Id, null, CancellationToken.None);
        var secondRequest = JObject.Parse(_transport.RequestPayloads[1]);

        Assert.Equal(ExecutionStatus.Success, secondResult.Status);
        Assert.Equal("updated_at:>'2026-10-15T09:15:00Z' financial_status:paid", secondRequest["variables"]!["search"]!.ToString());
        Assert.Empty(await ChildrenOfAsync(secondExecution));
    }

    [Fact]
    public void Order_create_drops_already_imported_orders_before_the_fan_out()
    {
        // The cross-reference filter is what keeps the overlap window (and any replay) from calling
        // Business Central for orders that are already there.
        var flow = LoadBuiltFlow(OrderCreateFlow);

        var filter = Assert.Single(flow.Nodes, node => node.StepType == "CrossReferenceFilter");
        var config = JObject.Parse(filter.StepConfig);
        Assert.Equal("shopify-orders-imported", config["listName"]!.ToString());
        Assert.Equal("flowState.FetchShopifyOrders.orders", config["arrayPath"]!.ToString());
        Assert.Equal("SkipExisting", config["filterMode"]!.ToString());
        var keyPaths = config["keyPaths"]!.Values<string>().ToList();
        Assert.Equal("id", Assert.Single(keyPaths));

        // It must sit after the cursor card (so the watermark still advances over everything fetched)
        // and before the fan-out (which consumes the filtered array).
        var watermark = flow.Nodes.Single(node => node.StepType == "PersistedState");
        var fanOut = flow.Nodes.Single(node => node.StepType == "FanOut");
        Assert.Contains(flow.Edges, edge => edge.SourceNodeId == watermark.Id && edge.TargetNodeId == filter.Id);
        Assert.Contains(flow.Edges, edge => edge.SourceNodeId == filter.Id && edge.TargetNodeId == fanOut.Id);
        Assert.Equal("flowState.FilterAlreadyImportedOrders", JObject.Parse(fanOut.StepConfig)["fanOut"]!["arrayPath"]!.ToString());
    }

    [Fact]
    public async Task Order_pull_skips_orders_already_recorded_in_the_ledger()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState("ordersWatermark", "2026-10-15T08:00:00Z"));
        var execution = SeedExecution(flow);

        // A previous run already imported the first order.
        _crossReferences.SeedKeys("shopify-orders-imported", "gid://shopify/Order/5100001001");

        _transport.Enqueue(200, """
        { "data": { "orders": { "pageInfo": { "hasNextPage": false, "endCursor": "c1" }, "edges": [
            { "node": { "id": "gid://shopify/Order/5100001001", "name": "#1001", "updatedAt": "2026-10-15T09:10:00Z" } },
            { "node": { "id": "gid://shopify/Order/5100001002", "name": "#1002", "updatedAt": "2026-10-15T09:30:00Z" } }
        ] } } }
        """);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        var filterStep = _executions.StepExecutions.First(step => step.NodeName == "FilterAlreadyImportedOrders");
        var remaining = JArray.Parse(filterStep.ResponsePayload);
        var only = Assert.IsType<JObject>(Assert.Single(remaining));
        Assert.Equal("gid://shopify/Order/5100001002", only["id"]!.ToString());

        var children = await ChildrenOfAsync(execution);
        Assert.Single(children);
        // The fan-out item is the filtered order, not the one that was already in the ledger.
        Assert.Contains("gid://shopify/Order/5100001002", children[0].FanOutItemJson);

        // The cursor still advanced past everything that was fetched, filtered or not.
        var persisted = ParseStateRaw(flow.PersistedStateJson);
        Assert.Equal("2026-10-15T09:30:00Z", persisted["ordersWatermark"]!.ToString());
    }

    [Fact]
    public async Task Order_create_child_run_posts_the_business_central_sales_order_contract()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState());
        var createNode = flow.Nodes.Single(node => node.NodeName == "CreateBcSalesOrder");

        var parentExecution = SeedExecution(flow);
        var childExecution = new FlowExecution
        {
            FlowId = flow.Id,
            TriggerSource = "FanOut",
            ParentExecutionId = parentExecution.Id
        };
        _executions.Seed(childExecution);

        var flowState = new JObject { ["order"] = JObject.Parse(ShopifyOrderItem("gid://shopify/Order/5100001001", "#1001", "2026-10-15T09:10:00Z", 2)) };
        _transport.Enqueue(201, """
        { "id": "0f1c", "shopifyOrderId": "gid://shopify/Order/5100001001", "shopifyOrderName": "#1001",
          "status": "Created", "salesOrderNo": "101234", "createdSalesOrder": true, "errorMessage": "" }
        """);

        var result = await RunFromNodesAsync(flow, childExecution, new[] { createNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("companies(" + BcCompanyId + ")/fintegratorSalesOrders", _transport.RequestedUrls[0]);
        Assert.Equal(Guid.Parse(BcConnectionId), _transport.ConnectionIds[0]);

        var body = JObject.Parse(_transport.RequestPayloads[0]);
        Assert.Equal("gid://shopify/Order/5100001001", body["shopifyOrderId"]!.ToString());
        Assert.Equal("#1001", body["shopifyOrderName"]!.ToString());
        Assert.Equal("USD", body["currencyCode"]!.ToString());
        Assert.Equal("2026-10-14", body["orderDate"]!.ToString());
        Assert.Equal("Springfield", body["shippingCity"]!.ToString());
        Assert.Equal("US", body["shippingCountryCode"]!.ToString());

        var lines = Assert.IsType<JArray>(body["fintegratorSalesOrderLines"]);
        var line = Assert.IsType<JObject>(Assert.Single(lines));
        Assert.Equal("TSHIRT-BLK-M", line["sku"]!.ToString());
        Assert.Equal(2m, line["quantity"]!.Value<decimal>());
        Assert.Equal(29.95m, line["unitPrice"]!.Value<decimal>());
        Assert.Equal(10000, line["lineNo"]!.Value<int>());

        Assert.Equal("Shopify #1001 -> BC 101234", result.Identifier);
        Assert.Contains("Created Business Central sales order 101234", result.Message);
        Assert.Equal(201, _executions.StepExecutions.Last(step => step.NodeName == "CreateBcSalesOrder").HttpStatusCode);
    }

    [Fact]
    public async Task Order_create_child_run_treats_a_business_central_duplicate_as_success()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState());
        var createNode = flow.Nodes.Single(node => node.NodeName == "CreateBcSalesOrder");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["order"] = JObject.Parse(ShopifyOrderItem("gid://shopify/Order/5100001001", "#1001", "2026-10-15T09:10:00Z", 2)) };
        _transport.Enqueue(201, """
        { "status": "Duplicate", "salesOrderNo": "101234", "createdSalesOrder": false, "errorMessage": "" }
        """);

        var result = await RunFromNodesAsync(flow, execution, new[] { createNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Contains("already imported as Business Central sales order 101234", result.Message);
    }

    [Fact]
    public async Task Order_create_child_run_fails_when_business_central_reports_no_sales_order()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState());
        var createNode = flow.Nodes.Single(node => node.NodeName == "CreateBcSalesOrder");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["order"] = JObject.Parse(ShopifyOrderItem("gid://shopify/Order/5100001001", "#1001", "2026-10-15T09:10:00Z", 2)) };
        _transport.Enqueue(201, """
        { "status": "Failed", "salesOrderNo": "", "createdSalesOrder": false, "errorMessage": "Shopify SKU NOPE does not match any Business Central item." }
        """);

        var result = await RunFromNodesAsync(flow, execution, new[] { createNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("Shopify SKU NOPE does not match any Business Central item.", result.ErrorMessage);

        var deadLetter = Assert.Single(_executions.DeadLetters);
        Assert.Contains("did not return a sales order number", deadLetter.ErrorMessage);
    }

    [Fact]
    public async Task Order_create_child_run_reports_a_missing_company_id_actionably()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState());
        var createNode = flow.Nodes.Single(node => node.NodeName == "CreateBcSalesOrder");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["order"] = JObject.Parse(ShopifyOrderItem("gid://shopify/Order/5100001001", "#1001", "2026-10-15T09:10:00Z", 2)) };
        var state = new JObject { ["shopifyApiVersion"] = "2026-07" };

        var result = await RunFromNodesAsync(flow, execution, new[] { createNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), state.ToString(Newtonsoft.Json.Formatting.None));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("bcCompanyId", result.ErrorMessage);
        Assert.Empty(_transport.RequestedUrls);
    }

    [Fact]
    public async Task Order_shipment_child_run_creates_a_fulfillment_with_the_business_central_tracking()
    {
        var dto = LoadBuiltFlow(OrderShipmentFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "FetchShopifyFulfillmentOrders");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["shipment"] = JObject.Parse(BcShipmentItem()) };

        _transport.Enqueue(200, """
        { "data": { "order": { "id": "gid://shopify/Order/5100001001", "name": "#1001", "displayFulfillmentStatus": "UNFULFILLED",
          "fulfillmentOrders": { "nodes": [ { "id": "gid://shopify/FulfillmentOrder/6001", "status": "OPEN", "requestStatus": "UNSUBMITTED",
            "lineItems": { "nodes": [ { "id": "gid://shopify/FulfillmentOrderLineItem/6501", "remainingQuantity": 2,
              "lineItem": { "id": "gid://shopify/LineItem/9001", "sku": "TSHIRT-BLK-M" } } ] } } ] } } } }
        """);
        _transport.Enqueue(200, """
        { "data": { "fulfillmentCreateV2": { "fulfillment": { "id": "gid://shopify/Fulfillment/7001", "status": "SUCCESS",
          "trackingInfo": [ { "number": "1Z999AA10123456784", "company": "UPS", "url": null } ] }, "userErrors": [] } } }
        """);
        _transport.Enqueue(200, """{ "id": "66666666-6666-6666-6666-666666666666", "entryNo": 11, "shopifyPushed": true }""");

        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(3, _transport.RequestedUrls.Count);

        var query = JObject.Parse(_transport.RequestPayloads[0]);
        Assert.Equal("gid://shopify/Order/5100001001", query["variables"]!["id"]!.ToString());
        Assert.Contains("fulfillmentOrders", query["query"]!.ToString());

        var mutation = JObject.Parse(_transport.RequestPayloads[1]);
        Assert.Contains("fulfillmentCreateV2", mutation["query"]!.ToString());

        var fulfillment = mutation["variables"]!["fulfillment"]!;
        var byOrder = Assert.IsType<JArray>(fulfillment["lineItemsByFulfillmentOrder"]);
        var group = Assert.IsType<JObject>(Assert.Single(byOrder));
        Assert.Equal("gid://shopify/FulfillmentOrder/6001", group["fulfillmentOrderId"]!.ToString());

        var item = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(group["fulfillmentOrderLineItems"])));
        Assert.Equal("gid://shopify/FulfillmentOrderLineItem/6501", item["id"]!.ToString());
        Assert.Equal(2m, item["quantity"]!.Value<decimal>());

        Assert.Equal("1Z999AA10123456784", fulfillment["trackingInfo"]!["number"]!.ToString());
        Assert.Equal("UPS", fulfillment["trackingInfo"]!["company"]!.ToString());
        Assert.False(fulfillment["notifyCustomer"]!.Value<bool>());

        // The feed row is flagged in Business Central, which is what makes a replay safe.
        Assert.Equal(
            "companies(" + BcCompanyId + ")/fintegratorShippedOrders(66666666-6666-6666-6666-666666666666)",
            _transport.RequestedUrls[2]);
        Assert.True(JObject.Parse(_transport.RequestPayloads[2])["shopifyPushed"]!.Value<bool>());

        Assert.Equal("BC shipment 102301 -> Shopify #1001", result.Identifier);
        Assert.Contains("Marked Shopify order #1001 as shipped", result.Message);
    }

    [Fact]
    public async Task Order_shipment_child_run_skips_a_feed_row_that_was_already_pushed()
    {
        var dto = LoadBuiltFlow(OrderShipmentFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "FetchShopifyFulfillmentOrders");
        var execution = SeedExecution(flow);

        var shipment = JObject.Parse(BcShipmentItem());
        shipment["shopifyPushed"] = true;
        var flowState = new JObject { ["shipment"] = shipment };

        _transport.Enqueue(200, """
        { "data": { "order": { "id": "gid://shopify/Order/5100001001", "name": "#1001", "displayFulfillmentStatus": "PARTIALLY_FULFILLED",
          "fulfillmentOrders": { "nodes": [ { "id": "gid://shopify/FulfillmentOrder/6001", "status": "OPEN", "requestStatus": "UNSUBMITTED",
            "lineItems": { "nodes": [ { "id": "gid://shopify/FulfillmentOrderLineItem/6501", "remainingQuantity": 2,
              "lineItem": { "id": "gid://shopify/LineItem/9001", "sku": "TSHIRT-BLK-M" } } ] } } ] } } } }
        """);

        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        // Only the lookup call: the row already carries the pushed flag, so nothing is sent to Shopify.
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Single(_transport.RequestedUrls);
        Assert.Contains("needs no push", result.Message);
    }

    [Fact]
    public async Task Order_shipment_child_run_skips_when_shopify_has_no_open_fulfillment_order()
    {
        var dto = LoadBuiltFlow(OrderShipmentFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "FetchShopifyFulfillmentOrders");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["shipment"] = JObject.Parse(BcShipmentItem()) };
        _transport.Enqueue(200, """
        { "data": { "order": { "id": "gid://shopify/Order/5100001001", "name": "#1001", "displayFulfillmentStatus": "FULFILLED",
          "fulfillmentOrders": { "nodes": [ { "id": "gid://shopify/FulfillmentOrder/6001", "status": "CLOSED", "requestStatus": "CLOSED",
            "lineItems": { "nodes": [ { "id": "gid://shopify/FulfillmentOrderLineItem/6501", "remainingQuantity": 0,
              "lineItem": { "id": "gid://shopify/LineItem/9001", "sku": "TSHIRT-BLK-M" } } ] } } ] } } } }
        """);

        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Single(_transport.RequestedUrls);
        Assert.Contains("needs no push", result.Message);
        Assert.Contains("FULFILLED", result.Message);
        Assert.Equal("BC shipment 102301 -> Shopify #1001", result.Identifier);
    }

    [Fact]
    public async Task Order_shipment_child_run_fails_actionably_when_the_shopify_order_is_gone()
    {
        var dto = LoadBuiltFlow(OrderShipmentFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "FetchShopifyFulfillmentOrders");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["shipment"] = JObject.Parse(BcShipmentItem()) };
        _transport.Enqueue(200, """{ "data": { "order": null } }""");

        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("Shopify returned no order", result.ErrorMessage);
    }

    [Fact]
    public async Task Inventory_child_run_sets_the_absolute_quantity_with_a_stable_idempotency_key()
    {
        var dto = LoadBuiltFlow(InventoryFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "LookupShopifyVariant");

        var flowState = new JObject { ["change"] = JObject.Parse(InventoryChangeItem("MUG-WHITE", 42)) };
        var variantLookup = """
        { "data": { "productVariants": { "nodes": [ { "id": "gid://shopify/ProductVariant/8001", "sku": "MUG-WHITE",
            "inventoryItem": { "id": "gid://shopify/InventoryItem/8101", "tracked": true } } ] },
          "locations": { "nodes": [ { "id": "gid://shopify/Location/9001", "name": "Main Warehouse", "isActive": true } ] } } }
        """;
        var setPayload = """
        { "data": { "inventorySetQuantities": { "inventoryAdjustmentGroup": { "reason": "correction",
            "changes": [ { "name": "available", "delta": 10 } ] }, "userErrors": [] } } }
        """;

        var firstExecution = SeedExecution(flow);
        _transport.Enqueue(200, variantLookup);
        _transport.Enqueue(200, setPayload);
        var first = await RunFromNodesAsync(flow, firstExecution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        var secondExecution = SeedExecution(flow);
        _transport.Enqueue(200, variantLookup);
        _transport.Enqueue(200, setPayload);
        await RunFromNodesAsync(flow, secondExecution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Success, first.Status);
        Assert.Equal(4, _transport.RequestedUrls.Count);

        var lookup = JObject.Parse(_transport.RequestPayloads[0]);
        Assert.Equal("sku:MUG-WHITE", lookup["variables"]!["search"]!.ToString());

        var mutationQuery = JObject.Parse(_transport.RequestPayloads[1])["query"]!.ToString();
        Assert.Contains("inventorySetQuantities", mutationQuery);
        Assert.Contains("@idempotent", mutationQuery);

        var input = JObject.Parse(_transport.RequestPayloads[1])["variables"]!["input"]!;
        Assert.Equal("available", input["name"]!.ToString());
        Assert.Equal("correction", input["reason"]!.ToString());
        Assert.True(input["ignoreCompareQuantity"]!.Value<bool>());

        var quantity = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(input["quantities"])));
        Assert.Equal("gid://shopify/InventoryItem/8101", quantity["inventoryItemId"]!.ToString());
        Assert.Equal("gid://shopify/Location/9001", quantity["locationId"]!.ToString());
        Assert.Equal(42m, quantity["quantity"]!.Value<decimal>());

        var firstKey = JObject.Parse(_transport.RequestPayloads[1])["variables"]!["idempotencyKey"]!.ToString();
        var secondKey = JObject.Parse(_transport.RequestPayloads[3])["variables"]!["idempotencyKey"]!.ToString();
        Assert.Equal(36, firstKey.Length);
        Assert.Equal(firstKey, secondKey);

        Assert.Equal("BC MUG-WHITE @ WH1 -> Shopify SKU MUG-WHITE", first.Identifier);
        Assert.Contains("set to 42", first.Message);
    }

    [Fact]
    public async Task Inventory_child_run_skips_an_item_without_a_shopify_variant()
    {
        var dto = LoadBuiltFlow(InventoryFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "LookupShopifyVariant");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["change"] = JObject.Parse(InventoryChangeItem("GHOST-SKU", 5)) };
        _transport.Enqueue(200, """
        { "data": { "productVariants": { "nodes": [] }, "locations": { "nodes": [ { "id": "gid://shopify/Location/9001", "name": "Main Warehouse", "isActive": true } ] } } }
        """);

        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), PersistedState());

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Single(_transport.RequestedUrls);
        Assert.Contains("no Shopify variant matches SKU GHOST-SKU", result.Message);
    }

    [Fact]
    public async Task Inventory_child_run_omits_the_idempotency_directive_for_api_versions_before_2026_01()
    {
        var dto = LoadBuiltFlow(InventoryFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "LookupShopifyVariant");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["change"] = JObject.Parse(InventoryChangeItem("MUG-WHITE", 42)) };
        _transport.Enqueue(200, """
        { "data": { "productVariants": { "nodes": [ { "id": "gid://shopify/ProductVariant/8001", "sku": "MUG-WHITE",
            "inventoryItem": { "id": "gid://shopify/InventoryItem/8101", "tracked": true } } ] },
          "locations": { "nodes": [ { "id": "gid://shopify/Location/9001", "name": "Main Warehouse", "isActive": true } ] } } }
        """);
        _transport.Enqueue(200, """
        { "data": { "inventorySetQuantities": { "inventoryAdjustmentGroup": { "changes": [] }, "userErrors": [] } } }
        """);

        var state = PersistedState(apiVersion: "2025-10");
        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), state);

        var mutation = JObject.Parse(_transport.RequestPayloads[1]);
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.DoesNotContain("@idempotent", mutation["query"]!.ToString());
        Assert.Null(mutation["variables"]!["idempotencyKey"]);
    }

    [Fact]
    public async Task Inventory_and_shipment_watermarks_advance_from_the_business_central_feed()
    {
        var shipmentDto = LoadBuiltFlow(OrderShipmentFlow);
        var shipmentFlow = SeedFlow(shipmentDto, PersistedStateWith(new JObject
        {
            ["shipmentsWatermark"] = "2026-10-15T09:00:00Z",
            ["shipmentsEntryNo"] = 5
        }));
        var shipmentExecution = SeedExecution(shipmentFlow);

        _transport.Enqueue(200, """
        { "value": [
            { "entryNo": 10, "lastModifiedDateTime": "2026-10-15T09:40:00Z", "salesShipmentNo": "102301", "shopifyOrderId": "gid://shopify/Order/1" },
            { "entryNo": 11, "lastModifiedDateTime": "2026-10-15T09:40:00Z", "salesShipmentNo": "102302", "shopifyOrderId": "gid://shopify/Order/1" }
        ] }
        """);

        var shipmentResult = await CreateExecutor().ExecuteFlowAsync(shipmentFlow.Id, shipmentExecution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, shipmentResult.Status);
        // entryNo is the cursor: an auto-increment integer cannot drift in precision or format the
        // way an ISO timestamp can.
        Assert.Contains("$filter=" + Uri.EscapeDataString("entryNo gt 5"), _transport.RequestedUrls[0]);
        Assert.Contains(Uri.EscapeDataString("entryNo asc"), _transport.RequestedUrls[0]);
        Assert.Contains("$expand=fintegratorShippedOrderLines", _transport.RequestedUrls[0]);
        Assert.DoesNotContain("lastModifiedDateTime", _transport.RequestedUrls[0]);

        var shipmentState = ParseStateRaw(shipmentFlow.PersistedStateJson);
        Assert.Equal("2026-10-15T09:40:00Z", shipmentState["shipmentsWatermark"]!.ToString());
        Assert.Equal(11, shipmentState["shipmentsEntryNo"]!.Value<long>());

        // Two feed rows for the same order collapse into one child transaction.
        var shipmentChildren = await ChildrenOfAsync(shipmentExecution);
        var collapsedShipments = JObject.Parse(_executions.StepExecutions.First(step => step.NodeName == "LatestShipmentPerOrder").ResponsePayload);
        Assert.Single(shipmentChildren);
        Assert.Equal(2, collapsedShipments["sourceCount"]!.Value<int>());
        Assert.Equal(1, collapsedShipments["count"]!.Value<int>());

        var inventoryDto = LoadBuiltFlow(InventoryFlow);
        var inventoryFlow = SeedFlow(inventoryDto, PersistedState("inventoryEntryNo", 5));
        var inventoryExecution = SeedExecution(inventoryFlow);

        _transport.Enqueue(200, """
        { "value": [ { "entryNo": 21, "lastModifiedDateTime": "2026-10-15T09:46:00Z", "itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "locationCode": "WH1", "inventory": 42 } ] }
        """);

        var inventoryResult = await CreateExecutor().ExecuteFlowAsync(inventoryFlow.Id, inventoryExecution.Id, null, CancellationToken.None);
        var inventoryState = ParseStateRaw(inventoryFlow.PersistedStateJson);

        Assert.Equal(ExecutionStatus.Success, inventoryResult.Status);
        Assert.Contains("$filter=" + Uri.EscapeDataString("entryNo gt 5"), _transport.RequestedUrls[1]);
        Assert.Equal("2026-10-15T09:46:00Z", inventoryState["inventoryWatermark"]!.ToString());
        Assert.Equal(21, inventoryState["inventoryEntryNo"]!.Value<long>());
        Assert.Single(await ChildrenOfAsync(inventoryExecution));
    }

    [Fact]
    public async Task Inventory_batch_keeps_only_the_newest_movement_per_sku_and_location()
    {
        // inventorySetQuantities writes absolute values and fan-out children run concurrently, so the
        // batch must be collapsed before it is fanned out: otherwise a reordered pair of movements
        // would leave Shopify holding the older quantity.
        var dto = LoadBuiltFlow(InventoryFlow);
        var flow = SeedFlow(dto, PersistedState());
        var execution = SeedExecution(flow);

        _transport.Enqueue(200, """
        { "value": [
            { "entryNo": 31, "lastModifiedDateTime": "2026-10-15T10:00:00Z", "itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "locationCode": "WH1", "inventory": 30 },
            { "entryNo": 32, "lastModifiedDateTime": "2026-10-15T10:01:00Z", "itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "locationCode": "WH1", "inventory": 28 },
            { "entryNo": 33, "lastModifiedDateTime": "2026-10-15T10:02:00Z", "itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "locationCode": "WH2", "inventory": 7 },
            { "entryNo": 34, "lastModifiedDateTime": "2026-10-15T10:03:00Z", "itemNo": "TSHIRT-BLK-M", "sku": "TSHIRT-BLK-M", "locationCode": "WH1", "inventory": 4 }
        ] }
        """);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        var collapsed = JObject.Parse(_executions.StepExecutions.First(step => step.NodeName == "LatestInventoryChangePerSkuLocation").ResponsePayload);
        var changes = (JArray)collapsed["changes"]!;
        Assert.Equal(4, collapsed["sourceCount"]!.Value<int>());
        Assert.Equal(3, changes.Count);

        var mugWhiteWarehouse1 = changes.Single(change => change["sku"]!.ToString() == "MUG-WHITE" && change["locationCode"]!.ToString() == "WH1");
        Assert.Equal(28m, mugWhiteWarehouse1["inventory"]!.Value<decimal>());
        Assert.Equal(32, mugWhiteWarehouse1["entryNo"]!.Value<int>());

        Assert.Equal(3, (await ChildrenOfAsync(execution)).Count);
    }

    [Fact]
    public async Task Shipment_batch_merges_rows_per_order_and_keeps_the_newest_tracking()
    {
        var dto = LoadBuiltFlow(OrderShipmentFlow);
        var flow = SeedFlow(dto, PersistedState());
        var execution = SeedExecution(flow);

        _transport.Enqueue(200, """
        { "value": [
            { "entryNo": 41, "lastModifiedDateTime": "2026-10-15T10:00:00Z", "salesShipmentNo": "102401", "shopifyOrderId": "gid://shopify/Order/5100001001",
              "shopifyOrderName": "#1001", "trackingNo": "OLD-TRACK", "fintegratorShippedOrderLines": { } },
            { "entryNo": 41, "lastModifiedDateTime": "2026-10-15T10:00:00Z", "salesShipmentNo": "102401", "shopifyOrderId": "gid://shopify/Order/5100001001",
              "shopifyOrderName": "#1001", "trackingNo": "OLD-TRACK",
              "fintegratorShippedOrderLines": [ { "lineNo": 10000, "itemNo": "TSHIRT-BLK-M", "sku": "TSHIRT-BLK-M", "quantity": 1 } ] },
            { "entryNo": 42, "lastModifiedDateTime": "2026-10-15T10:05:00Z", "salesShipmentNo": "102402", "shopifyOrderId": "gid://shopify/Order/5100001001",
              "shopifyOrderName": "#1001", "trackingNo": "NEW-TRACK", "shippingAgentName": "DHL",
              "fintegratorShippedOrderLines": [ { "lineNo": 10000, "itemNo": "TSHIRT-BLK-M", "sku": "TSHIRT-BLK-M", "quantity": 1 } ] },
            { "entryNo": 43, "lastModifiedDateTime": "2026-10-15T10:06:00Z", "salesShipmentNo": "102403", "shopifyOrderId": "gid://shopify/Order/5100001002",
              "shopifyOrderName": "#1002", "trackingNo": "OTHER-TRACK",
              "fintegratorShippedOrderLines": [ { "lineNo": 10000, "itemNo": "MUG-WHITE", "sku": "MUG-WHITE", "quantity": 3 } ] }
        ] }
        """);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Success, result.Status);

        var collapsed = JObject.Parse(_executions.StepExecutions.First(step => step.NodeName == "LatestShipmentPerOrder").ResponsePayload);
        var shipments = (JArray)collapsed["shipments"]!;
        Assert.Equal(4, collapsed["sourceCount"]!.Value<int>());
        Assert.Equal(2, shipments.Count);

        var orderOne = shipments.Single(shipment => shipment["shopifyOrderId"]!.ToString() == "gid://shopify/Order/5100001001");
        Assert.Equal("NEW-TRACK", orderOne["trackingNo"]!.ToString());
        Assert.Equal("102402", orderOne["salesShipmentNo"]!.ToString());

        var lines = (JArray)orderOne["fintegratorShippedOrderLines"]!;
        var line = Assert.IsType<JObject>(Assert.Single(lines));
        Assert.Equal(2m, line["quantity"]!.Value<decimal>());

        Assert.Equal(2, (await ChildrenOfAsync(execution)).Count);
    }

    [Fact]
    public async Task Inventory_child_run_uses_the_business_central_location_map()
    {
        var dto = LoadBuiltFlow(InventoryFlow);
        var flow = SeedFlow(dto, PersistedState());
        var startNode = flow.Nodes.Single(node => node.NodeName == "LookupShopifyVariant");
        var execution = SeedExecution(flow);

        var flowState = new JObject { ["change"] = JObject.Parse(InventoryChangeItem("MUG-WHITE", 42)) };
        var state = PersistedStateWith(new JObject
        {
            ["shopifyLocationByBcLocation"] = new JObject { ["WH1"] = "gid://shopify/Location/7777" }
        });

        _transport.Enqueue(200, """
        { "data": { "productVariants": { "nodes": [ { "id": "gid://shopify/ProductVariant/8001", "sku": "MUG-WHITE",
            "inventoryItem": { "id": "gid://shopify/InventoryItem/8101", "tracked": true } } ] },
          "locations": { "nodes": [
            { "id": "gid://shopify/Location/9001", "name": "Main Warehouse", "isActive": true },
            { "id": "gid://shopify/Location/7777", "name": "Outlet", "isActive": true } ] } } }
        """);
        _transport.Enqueue(200, """
        { "data": { "inventorySetQuantities": { "inventoryAdjustmentGroup": { "changes": [] }, "userErrors": [] } } }
        """);

        var result = await RunFromNodesAsync(flow, execution, new[] { startNode.Id }, flowState.ToString(Newtonsoft.Json.Formatting.None), state);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        var input = JObject.Parse(_transport.RequestPayloads[1])["variables"]!["input"]!;
        var quantity = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(input["quantities"])));
        Assert.Equal("gid://shopify/Location/7777", quantity["locationId"]!.ToString());
        Assert.Contains("Outlet", result.Message);
    }

    [Fact]
    public async Task Order_pull_fails_when_shopify_truncates_the_line_items()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState());
        var execution = SeedExecution(flow);

        _transport.Enqueue(200, """
        { "data": { "orders": { "pageInfo": { "hasNextPage": false, "endCursor": "c1" }, "edges": [
            { "node": { "id": "gid://shopify/Order/1", "name": "#2000", "updatedAt": "2026-10-15T09:10:00Z",
              "lineItems": { "pageInfo": { "hasNextPage": true }, "edges": [ { "node": { "sku": "TSHIRT-BLK-M", "quantity": 1 } } ] } } }
        ] } } }
        """);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("more than 100 line items", result.ErrorMessage);
        Assert.Empty(await ChildrenOfAsync(execution));
    }

    [Fact]
    public async Task Order_pull_reports_graphql_throttling_actionably()
    {
        var dto = LoadBuiltFlow(OrderCreateFlow);
        var flow = SeedFlow(dto, PersistedState());
        var execution = SeedExecution(flow);

        // Shopify reports rate limiting as HTTP 200 plus errors, so it never reaches the transport
        // retry policy; the message has to explain what to do instead.
        _transport.Enqueue(200, """
        { "errors": [ { "message": "Throttled" } ], "extensions": { "cost": { "throttleStatus": { "currentlyAvailable": 0 } } } }
        """);

        var result = await CreateExecutor().ExecuteFlowAsync(flow.Id, execution.Id, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("rate-limited", result.ErrorMessage);
        Assert.Empty(await ChildrenOfAsync(execution));
    }

    // ------------------------------------------------------------------ test doubles

    private sealed class RecordingTransportEngine : ITransportEngine
    {
        private readonly Queue<(int StatusCode, string Response)> _responses = new();

        public List<string> RequestedUrls { get; } = new();
        public List<string> RequestPayloads { get; } = new();
        public List<Guid?> ConnectionIds { get; } = new();

        public void Enqueue(int statusCode, string response) => _responses.Enqueue((statusCode, response));

        public Task<TransportResponse> DispatchAsync(
            IntegrationStep step, string? payload, Guid? connectionId = null,
            CancellationToken cancellationToken = default, FlowTestContext? testContext = null)
        {
            RequestedUrls.Add(step.EndpointUrl);
            RequestPayloads.Add(payload ?? string.Empty);
            ConnectionIds.Add(connectionId);

            var response = _responses.Count > 0 ? _responses.Dequeue() : (StatusCode: 200, Response: "{}");
            return Task.FromResult(new TransportResponse(response.StatusCode, response.Response, new Dictionary<string, string[]>(), step.EndpointUrl));
        }
    }
}



