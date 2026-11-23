using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using SimpleIPaaS.Client.Services;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Components.FlowTests;

public sealed record VariableEditorRow
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed record TestCardVisual(string Badge, string Tone, string State)
{
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
    public bool NotReached => State == "notreached";
    public bool Failed => State == "failed";
}

public sealed record JsonPathOption(string Path, string Value);

public sealed class FlowTestSession : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Guid _flowId;
    private readonly IntegrationFlowDto _flow;
    private CancellationTokenSource? _pollCts;
    private bool _disposed;

    public FlowTestSession(HttpClient http, IntegrationFlowDto flow)
    {
        _http = http;
        _flow = flow;
        _flowId = flow.Id;
    }

    public Guid FlowId => _flowId;
    public IntegrationFlowDto Flow => _flow;
    public List<FlowTestCaseDto> Scenarios { get; private set; } = new();
    public FlowTestCaseDto? SelectedTest { get; private set; }
    public Guid SelectedId { get; private set; }
    public string ScenarioName { get; private set; } = "New scenario";
    public string ScenarioDescription { get; private set; } = string.Empty;
    public bool RequiredForPublish { get; private set; }
    public bool ScenarioEnabled { get; private set; } = true;
    public FlowTestDefinitionDto Definition { get; private set; } = new();
    public List<VariableEditorRow> Variables { get; private set; } = new();
    public string ExpectedRunResult => string.IsNullOrWhiteSpace(Definition.ExpectedRunResult)
        ? "Success"
        : Definition.ExpectedRunResult;
    public List<FlowTestRunDto> Runs { get; private set; } = new();
    public FlowTestRunDto? CurrentRun { get; private set; }
    public FlowTestEvaluationDto? Evaluation { get; private set; }
    public FlowTestRunDetailDto? Detail { get; private set; }
    public FlowTestSuiteRunDto? Suite { get; private set; }
    public Guid? SuiteRunId { get; private set; }
    public FlowTestSuiteStatusDto Status { get; private set; } = new();
    public bool IsBusy { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsDirty { get; private set; }
    public bool HasResult => Detail != null || Evaluation != null;
    public bool ResultsOpen { get; private set; }
    public bool SuiteMode => SuiteRunId.HasValue;
    public string? Error { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public List<string> Warnings { get; private set; } = new();
    public Guid? SelectedCardId { get; private set; }
    public string SelectedCardName { get; private set; } = string.Empty;
    public string InspectorTab { get; set; } = "Behavior";
    public bool KeepInspectorTab { get; set; }
    public string ScenarioSearch { get; set; } = string.Empty;
    public bool ScenarioPanelOpen { get; set; } = true;
    public bool InputsOpen { get; set; }
    public Dictionary<FlowHttpResponseOverrideDto, string> HeadersText { get; } = new();

    public event Action? Changed;

    public IReadOnlyList<FlowTestCaseDto> FilteredScenarios => string.IsNullOrWhiteSpace(ScenarioSearch)
        ? Scenarios
        : Scenarios.Where(test => test.Name.Contains(ScenarioSearch, StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task InitializeAsync()
    {
        await LoadScenariosAsync();
        await LoadStatusAsync();
        if (Scenarios.Count > 0) LoadScenario(Scenarios[0]);
        else NewScenario(markDirty: false);
        Notify();
    }

    public async Task LoadScenariosAsync()
    {
        Scenarios = await _http.GetFromJsonAsync<List<FlowTestCaseDto>>($"api/integrationflow/{_flowId}/test-cases")
                     ?? new List<FlowTestCaseDto>();
    }

    public async Task LoadStatusAsync()
    {
        try
        {
            Status = await _http.GetFromJsonAsync<FlowTestSuiteStatusDto>(
                         $"api/integrationflow/{_flowId}/test-suite/status") ?? new FlowTestSuiteStatusDto();
        }
        catch (HttpRequestException)
        {
            Status = new FlowTestSuiteStatusDto();
        }
    }

    // The blank scenario that opens by itself for a flow with no scenarios is only a starting point, not an edit.
    // Marking it dirty made leaving test mode ask "Discard unsaved changes?" when nothing had been touched.
    public void NewScenario(bool markDirty = true)
    {
        SelectedTest = null;
        SelectedId = Guid.Empty;
        ScenarioName = "New scenario";
        ScenarioDescription = string.Empty;
        RequiredForPublish = true;
        ScenarioEnabled = true;
        Definition = new FlowTestDefinitionDto
        {
            TriggerPayloadJson = "{}",
            Assertions = new List<FlowTestAssertionDto>
            {
                new()
                {
                    Type = "ExecutionStatus", Field = "Status", Operator = "Equals",
                    ExpectedValue = "Success", InvocationScope = "Every"
                }
            }
        };
        Variables = new();
        Runs = new();
        CurrentRun = null;
        Evaluation = null;
        Detail = null;
        Suite = null;
        SuiteRunId = null;
        Warnings.Clear();
        Error = null;
        Message = "New scenario. Configure cards, then save.";
        IsDirty = markDirty;
        SelectedCardId = null;
        ResultsOpen = false;
        Notify();
    }

    public void DuplicateScenario() => DuplicateScenario(SelectedTest);

    public void DuplicateScenario(FlowTestCaseDto? source)
    {
        if (source == null) return;
        SelectedTest = null;
        SelectedId = Guid.Empty;
        ScenarioName = $"Copy of {source.Name}";
        ScenarioDescription = source.Description;
        Definition = Clone(source.Definition ?? new());
        RequiredForPublish = false;
        ScenarioEnabled = true;
        Variables = Definition.Variables.Select(item => new VariableEditorRow { Name = item.Key, Value = item.Value }).ToList();
        IsDirty = true;
        Message = "Scenario duplicated. Save it as a new scenario.";
        Notify();
    }

    public void SetTriggerPayload(string value) { Definition.TriggerPayloadJson = value; IsDirty = true; Notify(); }
    public void AddVariable()
    {
        Variables.Add(new VariableEditorRow());
        IsDirty = true;
        Notify();
    }
    public void AddCrossReference()
    {
        Definition.CrossReferenceRows.Add(new FlowCrossReferenceFixtureDto { ValueJson = "{}" });
        IsDirty = true;
        Notify();
    }
    public void MarkChanged() => MarkDirty();

    public void LoadScenario(FlowTestCaseDto test)
    {
        SelectedTest = test;
        SelectedId = test.Id;
        ScenarioName = test.Name;
        ScenarioDescription = test.Description;
        RequiredForPublish = test.RequiredForPublish;
        ScenarioEnabled = test.Enabled;
        Definition = Clone(test.Definition ?? new());
        Variables = Definition.Variables.Select(item => new VariableEditorRow { Name = item.Key, Value = item.Value }).ToList();
        Runs = new();
        CurrentRun = null;
        Evaluation = null;
        Detail = null;
        Suite = null;
        SuiteRunId = null;
        Warnings.Clear();
        Error = null;
        IsDirty = false;
        SelectedCardId = null;
        ResultsOpen = false;
        _ = LoadRunsAsync();
        Notify();
    }

    public void MarkDirty()
    {
        IsDirty = true;
        Notify();
    }

    public void SetScenarioName(string value) { ScenarioName = value; IsDirty = true; Notify(); }
    public void SetDescription(string value) { ScenarioDescription = value; IsDirty = true; Notify(); }
    public void SetRequired(bool value) { RequiredForPublish = value; IsDirty = true; Notify(); }
    public void SetEnabled(bool value) { ScenarioEnabled = value; IsDirty = true; Notify(); }
    public void SetExpectedRunResult(string value)
    {
        Definition.ExpectedRunResult = value is "Failed" or "Any" ? value : "Success";
        IsDirty = true;
        Notify();
    }

    public bool ShouldSuggestExpectedFailure(Guid cardId)
    {
        var node = _flow.Nodes.FirstOrDefault(item => item.Id == cardId);
        return ShouldSuggestExpectedFailure(ExpectedRunResult, node?.StepType, node?.HttpErrorMode, cardId, Definition.Assertions);
    }

    public static bool ShouldSuggestExpectedFailure(
        string expectedRunResult,
        string? stepType,
        string? httpErrorMode,
        Guid cardId,
        IEnumerable<FlowTestAssertionDto> assertions)
    {
        if (expectedRunResult != "Success") return false;

        // Continue-mode HTTP failures are handled by the Error port and do not fail the run.
        if (stepType?.Equals("HttpAction", StringComparison.OrdinalIgnoreCase) == true
            && httpErrorMode?.Equals("Continue", StringComparison.OrdinalIgnoreCase) == true)
            return false;

        return assertions.Any(check =>
            check.CardId == cardId
            && !check.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)
            && (check.ExpectedValue.Equals("Failed", StringComparison.OrdinalIgnoreCase)
                || (check.Field.Equals("HttpStatusCode", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(check.ExpectedValue, out var status) && (status < 200 || status >= 300))
                || (check.Field.Equals("ErrorMessage", StringComparison.OrdinalIgnoreCase)
                    && !check.Operator.Equals("Missing", StringComparison.OrdinalIgnoreCase))));
    }
    public void SetPanelOpen(bool value) { ScenarioPanelOpen = value; Notify(); }
    public void SetInputsOpen(bool value) { InputsOpen = value; Notify(); }
    public void SetResultsOpen(bool value) { ResultsOpen = value; Notify(); }
    public void SetInspectorTab(string value) { InspectorTab = value; Notify(); }
    public void SelectCard(Guid? cardId, string name)
    {
        SelectedCardId = cardId;
        SelectedCardName = name;
        Notify();
    }
    public void CloseInspector() { SelectedCardId = null; SelectedCardName = string.Empty; Notify(); }

    public async Task<string?> SaveAsync()
    {
        if (IsBusy) return "A test is still running.";
        Error = null;
        Definition.Variables = Variables
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        if (!ApplyHeaders(out var headerError)) { Error = headerError; Notify(); return headerError; }
        if (!ValidateScenario(out var validationError)) { Error = validationError; Notify(); return validationError; }

        IsBusy = true;
        try
        {
            var dto = new FlowTestCaseDto
            {
                Id = SelectedId,
                FlowId = _flowId,
                Name = string.IsNullOrWhiteSpace(ScenarioName) ? "New scenario" : ScenarioName.Trim(),
                Description = ScenarioDescription ?? string.Empty,
                Enabled = ScenarioEnabled,
                RequiredForPublish = RequiredForPublish,
                Definition = Definition
            };
            var response = SelectedTest == null
                ? await _http.PostAsJsonAsync($"api/integrationflow/{_flowId}/test-cases", dto)
                : await _http.PutAsJsonAsync($"api/integrationflow/{_flowId}/test-cases/{SelectedId}", dto);
            if (!response.IsSuccessStatusCode)
            {
                Error = await ResponseErrorAsync(response);
                return Error;
            }
            SelectedTest = await response.Content.ReadFromJsonAsync<FlowTestCaseDto>();
            if (SelectedTest != null)
            {
                SelectedId = SelectedTest.Id;
                Definition = Clone(SelectedTest.Definition ?? new());
                ScenarioName = SelectedTest.Name;
                RequiredForPublish = SelectedTest.RequiredForPublish;
                ScenarioEnabled = SelectedTest.Enabled;
            }
            IsDirty = false;
            Message = "Scenario saved.";
            await LoadScenariosAsync();
            await LoadStatusAsync();
            return null;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public async Task RunAsync()
    {
        if (SelectedTest == null && SelectedId == Guid.Empty)
        {
            var saveError = await SaveAsync();
            if (saveError != null || SelectedId == Guid.Empty) return;
        }
        if (IsDirty)
        {
            var error = await SaveAsync();
            if (error != null) return;
        }

        StopPolling();
        _pollCts = new CancellationTokenSource();
        IsBusy = true;
        IsRunning = true;
        Error = null;
        Message = "Test running…";
        ResultsOpen = true;
        Suite = null;
        SuiteRunId = null;
        Notify();
        try
        {
            var response = await _http.PostAsync(
                $"api/integrationflow/{_flowId}/test-cases/{SelectedId}/run", null);
            if (!response.IsSuccessStatusCode)
            {
                Error = await ResponseErrorAsync(response);
                IsRunning = false;
                return;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("testRunId", out var runIdElement)
                || !Guid.TryParse(runIdElement.GetString(), out var testRunId))
            {
                Error = "The API did not return a test run id.";
                IsRunning = false;
                return;
            }
            await PollRunAsync(testRunId, _pollCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ErrorText.Describe(ex);
            IsRunning = false;
        }
        finally
        {
            IsBusy = false;
            IsRunning = false;
            Notify();
        }
    }

    public async Task RunSuiteAsync(bool requiredOnly = false)
    {
        if (IsDirty)
        {
            var error = await SaveAsync();
            if (error != null) return;
        }
        StopPolling();
        _pollCts = new CancellationTokenSource();
        IsBusy = true;
        IsRunning = true;
        Error = null;
        ResultsOpen = true;
        Suite = null;
        Notify();
        try
        {
            var response = await _http.PostAsJsonAsync(
                $"api/integrationflow/{_flowId}/test-suite/run",
                new FlowTestSuiteRunRequestDto { RequiredOnly = requiredOnly });
            if (!response.IsSuccessStatusCode)
            {
                Error = await ResponseErrorAsync(response);
                IsRunning = false;
                return;
            }
            var start = await response.Content.ReadFromJsonAsync<FlowTestSuiteStartDto>();
            if (start == null)
            {
                Error = "The API did not return a suite run id.";
                IsRunning = false;
                return;
            }
            SuiteRunId = start.SuiteRunId;
            await PollSuiteAsync(start.SuiteRunId, _pollCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ErrorText.Describe(ex);
            IsRunning = false;
        }
        finally
        {
            IsBusy = false;
            IsRunning = false;
            await ReloadScenariosPreservingSelectionAsync();
            await LoadStatusAsync();
            if (Suite != null)
                Message = $"Suite finished: {Suite.Passed} passed, {Suite.Failed} failed.";
            Notify();
        }
    }

    private async Task ReloadScenariosPreservingSelectionAsync()
    {
        var selectedId = SelectedId;
        await LoadScenariosAsync();
        if (selectedId != Guid.Empty && !IsDirty)
        {
            var selected = Scenarios.FirstOrDefault(item => item.Id == selectedId);
            if (selected != null && SelectedTest != null)
            {
                SelectedTest.LastRunAt = selected.LastRunAt;
                SelectedTest.LastPassedAt = selected.LastPassedAt;
                SelectedTest.Enabled = selected.Enabled;
                SelectedTest.RequiredForPublish = selected.RequiredForPublish;
            }
        }
    }

    public async Task LoadRunsAsync()
    {
        if (SelectedId == Guid.Empty) return;
        try
        {
            Runs = await _http.GetFromJsonAsync<List<FlowTestRunDto>>(
                       $"api/integrationflow/{_flowId}/test-cases/{SelectedId}/runs") ?? new();
            if (CurrentRun == null && Runs.Count > 0)
                await LoadRunAsync(Runs[0].Id);
        }
        catch (HttpRequestException) { }
        Notify();
    }

    public async Task LoadRunAsync(Guid testRunId)
    {
        try
        {
            var detail = await _http.GetFromJsonAsync<FlowTestRunDetailDto>(
                $"api/integrationflow/{_flowId}/test-runs/{testRunId}");
            if (detail == null) return;
            ApplyDetail(detail);
            ResultsOpen = true;
            await LoadStatusAsync();
        }
        catch (HttpRequestException ex)
        {
            Error = ErrorText.Describe(ex);
        }
        Notify();
    }

    public async Task SelectRunAsync(Guid testRunId)
    {
        SuiteRunId = null;
        Suite = null;
        await LoadRunAsync(testRunId);
    }

    private async Task PollRunAsync(Guid testRunId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var detail = await _http.GetFromJsonAsync<FlowTestRunDetailDto>(
                $"api/integrationflow/{_flowId}/test-runs/{testRunId}", token);
            if (detail == null) return;
            ApplyDetail(detail);
            Notify();
            if (detail.Run.Status is "Passed" or "Failed" or "Cancelled") return;
            await Task.Delay(750, token);
        }
    }

    private async Task PollSuiteAsync(Guid suiteRunId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Suite = await _http.GetFromJsonAsync<FlowTestSuiteRunDto>(
                $"api/integrationflow/{_flowId}/test-suite/runs/{suiteRunId}", token);
            Notify();
            if (Suite == null || Suite.Status != "Running") return;
            await Task.Delay(750, token);
        }
    }

    private void ApplyDetail(FlowTestRunDetailDto detail)
    {
        Detail = detail;
        CurrentRun = detail.Run;
        Evaluation = detail.Evaluation;
        var index = Runs.FindIndex(run => run.Id == detail.Run.Id);
        if (index >= 0) Runs[index] = detail.Run;
        else Runs.Insert(0, detail.Run);

        if (detail.Run.Status is "Passed" or "Failed" or "Cancelled")
        {
            var finishedAt = detail.Run.CompletedAt ?? detail.Run.StartedAt;
            var scenario = Scenarios.FirstOrDefault(item => item.Id == detail.Run.TestCaseId);
            if (scenario != null)
            {
                scenario.LastRunAt = finishedAt;
                if (detail.Run.Status == "Passed") scenario.LastPassedAt = finishedAt;
            }
            if (SelectedTest != null && SelectedTest.Id == detail.Run.TestCaseId)
            {
                SelectedTest.LastRunAt = finishedAt;
                if (detail.Run.Status == "Passed") SelectedTest.LastPassedAt = finishedAt;
            }
            // The drawer owns the final headline; never leave the transient "Test running…"
            // label in the toolbar after a terminal state.
            Message = string.Empty;
        }
    }

    public async Task LoadSuiteAsync(Guid suiteRunId)
    {
        SuiteRunId = suiteRunId;
        Suite = await _http.GetFromJsonAsync<FlowTestSuiteRunDto>(
            $"api/integrationflow/{_flowId}/test-suite/runs/{suiteRunId}");
        ResultsOpen = true;
        Notify();
    }

    public async Task<List<FlowExecutionDto>> LoadExecutionCandidatesAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<List<FlowExecutionDto>>(
                       $"api/executions?flowId={_flowId}&page=1&pageSize=50") ?? new();
        }
        catch (HttpRequestException)
        {
            return new List<FlowExecutionDto>();
        }
    }

    public async Task StartFromExecutionAsync(Guid executionId)
    {
        Error = null;
        try
        {
            var response = await _http.PostAsync(
                $"api/integrationflow/{_flowId}/test-cases/from-execution/{executionId}", null);
            if (!response.IsSuccessStatusCode)
            {
                Error = await ResponseErrorAsync(response);
                return;
            }
            var draft = await response.Content.ReadFromJsonAsync<FlowTestCaseDto>();
            if (draft == null) return;
            SelectedTest = null;
            SelectedId = Guid.Empty;
            ScenarioName = draft.Name;
            ScenarioDescription = draft.Description;
            RequiredForPublish = false;
            ScenarioEnabled = true;
            Definition = draft.Definition;
            Variables = Definition.Variables.Select(item => new VariableEditorRow { Name = item.Key, Value = item.Value }).ToList();
            Warnings = draft.Warnings;
            Runs = new();
            CurrentRun = null;
            Evaluation = null;
            Detail = null;
            IsDirty = true;
            Message = "Draft created from the selected transaction. Review it, then save.";
            Notify();
        }
        catch (Exception ex)
        {
            Error = ErrorText.Describe(ex);
            Notify();
        }
    }

    public async Task DeleteScenarioAsync(FlowTestCaseDto test)
    {
        var response = await _http.DeleteAsync($"api/integrationflow/{_flowId}/test-cases/{test.Id}");
        if (!response.IsSuccessStatusCode)
        {
            Error = await ResponseErrorAsync(response);
            Notify();
            return;
        }
        if (SelectedId == test.Id) NewScenario();
        await LoadScenariosAsync();
        await LoadStatusAsync();
        Notify();
    }

    public async Task SetEnabledAsync(FlowTestCaseDto test, bool enabled)
    {
        test.Enabled = enabled;
        test.Definition ??= new FlowTestDefinitionDto();
        var response = await _http.PutAsJsonAsync(
            $"api/integrationflow/{_flowId}/test-cases/{test.Id}", test);
        if (!response.IsSuccessStatusCode)
        {
            test.Enabled = !enabled;
            Error = await ResponseErrorAsync(response);
        }
        else if (SelectedId == test.Id)
        {
            ScenarioEnabled = enabled;
        }
        await LoadScenariosAsync();
        await LoadStatusAsync();
        Notify();
    }

    public FlowTestCardOverrideDto GetOverride(Guid cardId, string cardName)
    {
        var item = Definition.CardOverrides.FirstOrDefault(candidate => candidate.CardId == cardId);
        if (item != null) return item;
        item = new FlowTestCardOverrideDto { CardId = cardId, CardName = cardName };
        Definition.CardOverrides.Add(item);
        return item;
    }

    public bool IsSupplied(Guid cardId) =>
        Definition.CardOverrides.Any(item => item.CardId == cardId
            && item.Mode.Equals("Supply", StringComparison.OrdinalIgnoreCase));

    public void SetCardMode(Guid cardId, string cardName, bool supply)
    {
        var item = GetOverride(cardId, cardName);
        item.Mode = supply ? "Supply" : "Run";
        if (supply && string.IsNullOrWhiteSpace(item.OutputJson)) item.OutputJson = "{}";
        MarkDirty();
    }

    public List<FlowHttpResponseOverrideDto> GetHttpResponses(Guid cardId)
    {
        var key = cardId.ToString();
        if (!Definition.HttpResponses.TryGetValue(key, out var responses))
            Definition.HttpResponses[key] = responses = new();
        foreach (var response in responses)
            if (!HeadersText.ContainsKey(response))
                HeadersText[response] = JsonSerializer.Serialize(response.Headers, new JsonSerializerOptions { WriteIndented = true });
        return responses;
    }

    public void AddHttpResponse(Guid cardId)
    {
        GetHttpResponses(cardId).Add(new FlowHttpResponseOverrideDto
        {
            StatusCode = 200,
            ResponseBody = "{}",
            ResponseFormat = "Json"
        });
        MarkDirty();
    }

    public void RemoveHttpResponse(Guid cardId, FlowHttpResponseOverrideDto response)
    {
        GetHttpResponses(cardId).Remove(response);
        HeadersText.Remove(response);
        MarkDirty();
    }

    public void MoveHttpResponse(Guid cardId, FlowHttpResponseOverrideDto response, int delta)
    {
        var list = GetHttpResponses(cardId);
        var index = list.IndexOf(response);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count) return;
        list.RemoveAt(index);
        list.Insert(target, response);
        MarkDirty();
    }

    public void UseLastActualOutput(Guid cardId, string cardName)
    {
        var card = Evaluation?.Cards.FirstOrDefault(candidate => candidate.CardId == cardId);
        if (card == null || string.IsNullOrWhiteSpace(card.Output)) return;
        if (IsHttpCard(cardId))
        {
            var responses = GetHttpResponses(cardId);
            responses.Add(new FlowHttpResponseOverrideDto
            {
                StatusCode = card.HttpStatusCode ?? 200,
                ResponseBody = card.Output,
                ResponseFormat = TryParseJson(card.Output, out _) ? "Json" : "Text"
            });
        }
        else
        {
            var item = GetOverride(cardId, cardName);
            item.Mode = "Supply";
            item.OutputFormat = TryParseJson(card.Output, out _) ? "Json" : "Text";
            item.OutputJson = card.Output;
            item.StatusCode = card.HttpStatusCode;
        }
        MarkDirty();
        Message = "Copied the last actual output into this scenario.";
    }

    public bool IsHttpCard(Guid cardId) =>
        _flow.Nodes.Any(node => node.Id == cardId
            && node.StepType.Equals("HttpAction", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<FlowTestAssertionDto> ChecksFor(Guid cardId, string cardName) =>
        Definition.Assertions.Where(check =>
            !check.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)
            && (check.CardId == cardId
                || (check.CardId == null
                    && !string.IsNullOrWhiteSpace(cardName)
                    && check.NodeName.Equals(cardName, StringComparison.OrdinalIgnoreCase))));

    public string CardNameForCheck(FlowTestAssertionDto check) =>
        string.IsNullOrWhiteSpace(check.NodeName) ? "This card" : check.NodeName;

    public FlowTestAssertionDto AddCheck(Guid cardId, string cardName)
    {
        var check = new FlowTestAssertionDto
        {
            Type = "CardStatus",
            CardId = cardId,
            NodeName = cardName,
            Field = "Status",
            Operator = "Equals",
            ExpectedValue = "Success",
            InvocationScope = "Every"
        };
        Definition.Assertions.Add(check);
        MarkDirty();
        return check;
    }

    public void RemoveCheck(FlowTestAssertionDto check)
    {
        Definition.Assertions.Remove(check);
        MarkDirty();
    }

    public FlowTestCardResultDto? CardResult(Guid cardId) =>
        Evaluation?.Cards.FirstOrDefault(card => card.CardId == cardId);

    public IReadOnlyList<JsonPathOption> PathOptions(Guid cardId)
    {
        var card = CardResult(cardId);
        if (card == null) return Array.Empty<JsonPathOption>();
        return FlattenPaths(card.Output, "output")
            .Concat(FlattenPaths(card.Input, "input"))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public void SetCheckPath(FlowTestAssertionDto check, JsonPathOption option)
    {
        check.Path = option.Path.StartsWith("output.", StringComparison.OrdinalIgnoreCase)
            ? option.Path["output.".Length..]
            : option.Path;
        check.Field = option.Path.StartsWith("input.", StringComparison.OrdinalIgnoreCase)
            ? "ReceivedInput"
            : "ResponsePayload";
        check.ExpectedValue = option.Value;
        MarkDirty();
    }

    public TestCardVisual GetCardVisual(Guid cardId)
    {
        if (IsRunning)
        {
            var progress = Detail?.Progress.FirstOrDefault(item => item.CardId == cardId);
            if (progress != null)
            {
                return progress.Status switch
                {
                    "InProgress" => new TestCardVisual("Running…", "running", "running"),
                    "Failed" => new TestCardVisual("Failed", "failed", "failed"),
                    "Success" => new TestCardVisual("Passed", "passed", "passed"),
                    _ => new TestCardVisual(progress.Status, progress.Status.ToLowerInvariant(), progress.Status.ToLowerInvariant())
                };
            }
        }

        var card = CardResult(cardId);
        if (card != null)
        {
            return card.Status switch
            {
                "Failed" => new TestCardVisual("Failed", "failed", "failed"),
                "NotReached" => new TestCardVisual("Not reached", "notreached", "notreached"),
                "Supplied" => new TestCardVisual("Supplied", "supplied", "supplied"),
                "Fixture" => new TestCardVisual("Fixture", "fixture", "fixture"),
                _ => new TestCardVisual("Passed", "passed", "passed")
            };
        }

        var checks = ChecksFor(cardId, string.Empty).Count();
        var checksSuffix = checks > 0
            ? $" · {checks} check{(checks == 1 ? string.Empty : "s")}"
            : string.Empty;
        if (IsSupplied(cardId))
            return new TestCardVisual($"Supplied{checksSuffix}", "supplied", "supplied");
        if (IsHttpCard(cardId))
        {
            var count = GetHttpResponses(cardId).Count;
            if (count > 0)
                return new TestCardVisual($"Fixture ×{count}{checksSuffix}", "fixture", "fixture");
            return new TestCardVisual($"Needs response{checksSuffix}", "needs-response", "needs-response");
        }

        return checks > 0
            ? new TestCardVisual($"{checks} check{(checks == 1 ? string.Empty : "s")}", "checks", "checks")
            : new TestCardVisual(string.Empty, string.Empty, string.Empty);
    }

    public string ScenarioState(FlowTestCaseDto test)
    {
        if (test.Id == SelectedId && IsRunning) return "running";
        if (test.LastRunAt == null) return "never";
        if (test.LastPassedAt == null || test.LastPassedAt < test.LastRunAt) return "failed";
        if (_flow.UpdatedAt > test.LastPassedAt) return "stale";
        return "passed";
    }

    public static string RelativeTime(DateTime? value)
    {
        if (!value.HasValue) return "Never run";
        var delta = DateTime.UtcNow - value.Value;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        return value.Value.ToLocalTime().ToString("MMM d");
    }

    public static string FormatJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException)
        {
            return value;
        }
    }

    public static string DescribeOperator(string? op) => op?.Replace(" ", string.Empty) switch
    {
        "Equals" => "equals",
        "NotEquals" => "does not equal",
        "Contains" => "contains",
        "NotContains" => "does not contain",
        "StartsWith" => "starts with",
        "EndsWith" => "ends with",
        "Exists" => "exists",
        "Missing" => "is missing",
        "GreaterThan" => "is greater than",
        "GreaterOrEqual" => "is at least",
        "LessThan" => "is less than",
        "LessOrEqual" => "is at most",
        "CollectionCount" => "has collection count",
        _ => op ?? "equals"
    };

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        IsRunning = false;
    }

    public void ClearError()
    {
        Error = null;
        Notify();
    }

    public void CloseResults()
    {
        ResultsOpen = false;
        Notify();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        await Task.CompletedTask;
    }

    private bool ValidateScenario(out string? error)
    {
        error = null;
        foreach (var response in Definition.HttpResponses.Values.SelectMany(item => item)
                     .Where(response => response.ResponseFormat.Equals("Json", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryParseJson(response.ResponseBody, out _))
            {
                error = "A JSON HTTP fixture is invalid.";
                return false;
            }
        }
        foreach (var item in Definition.CardOverrides.Where(item =>
                     item.Mode.Equals("Supply", StringComparison.OrdinalIgnoreCase)
                     && item.OutputFormat.Equals("Json", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryParseJson(item.OutputJson, out _))
            {
                error = $"The supplied JSON output for {item.CardName} is invalid.";
                return false;
            }
        }
        foreach (var node in _flow.Nodes.Where(node =>
                     node.StepType.Equals("HttpAction", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsSupplied(node.Id)) continue;
            if (!Definition.HttpResponses.TryGetValue(node.Id.ToString(), out var fixture) || fixture.Count == 0)
            {
                error = $"{node.NodeName} needs a response fixture before this scenario can run.";
                return false;
            }
        }
        return true;
    }

    private bool ApplyHeaders(out string? error)
    {
        error = null;
        foreach (var (response, text) in HeadersText)
        {
            if (string.IsNullOrWhiteSpace(text)) { response.Headers = new(); continue; }
            try
            {
                response.Headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(text)
                                   ?? new Dictionary<string, string[]>();
            }
            catch (JsonException)
            {
                error = "HTTP fixture headers must be a JSON object of string arrays.";
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<JsonPathOption> FlattenPaths(string? json, string prefix)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }
        using (document)
        {
            foreach (var item in Flatten(document.RootElement, prefix)) yield return item;
        }
    }

    private static IEnumerable<JsonPathOption> Flatten(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var item in Flatten(property.Value, $"{path}.{property.Name}"))
                        yield return item;
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in Flatten(item, $"{path}[{index}]")) yield return child;
                    index++;
                }
                break;
            default:
                yield return new JsonPathOption(path, element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText());
                break;
        }
    }

    private static bool TryParseJson(string? value, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(value ?? string.Empty);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static FlowTestDefinitionDto Clone(FlowTestDefinitionDto source) =>
        JsonSerializer.Deserialize<FlowTestDefinitionDto>(
            JsonSerializer.Serialize(source),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

    private static async Task<string> ResponseErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body)) return $"Request failed ({(int)response.StatusCode}).";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out var detail)) return detail.GetString() ?? body;
            if (document.RootElement.TryGetProperty("message", out var message)) return message.GetString() ?? body;
            if (document.RootElement.TryGetProperty("title", out var title)) return title.GetString() ?? body;
        }
        catch (JsonException) { }
        return body;
    }

    private void Notify() => Changed?.Invoke();
}
