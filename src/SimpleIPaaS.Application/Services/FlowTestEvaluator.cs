using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Application.Services;

public sealed record FlowTestEvaluation(bool Passed, IReadOnlyList<string> Failures, FlowTestEvaluationDto Details)
{
    public string ToJson() => JsonConvert.SerializeObject(Details);
}

public static class FlowTestEvaluator
{
    private const int MaxInputLength = 64 * 1024;

    public static FlowTestEvaluation Evaluate(
        FlowExecution execution,
        IEnumerable<StepExecution> stepSequence,
        FlowTestDefinition definition,
        IntegrationFlow? flow = null)
    {
        var steps = stepSequence.OrderBy(step => step.StartedAt).ToList();
        var failures = new List<string>();
        var checks = new List<FlowTestCheckResultDto>();
        var flowNodes = flow?.Nodes?.ToList() ?? new List<IntegrationStep>();
        var nodeById = flowNodes.Where(node => node.Id != Guid.Empty).GroupBy(node => node.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var groups = steps.GroupBy(step => step.StepId)
            .Select(group => new
            {
                StepId = group.Key,
                Steps = group.OrderBy(step => step.StartedAt).ToList()
            })
            .OrderBy(group => group.Steps[0].StartedAt)
            .ToList();

        var cards = new List<FlowTestCardResultDto>();
        var order = 0;
        foreach (var group in groups)
        {
            var first = group.Steps.First();
            var last = group.Steps.Last();
            var supplied = (definition.CardOverrides ?? new())
                .Any(item => item.CardId == group.StepId && item.Mode.Equals("Supply", StringComparison.OrdinalIgnoreCase));
            var fixture = (definition.HttpResponses ?? new()).Any(item =>
                Guid.TryParse(item.Key, out var id) && id == group.StepId
                && (!nodeById.TryGetValue(group.StepId, out var node) || node.StepType == StepType.HttpAction));
            var input = first.ReceivedInput ?? string.Empty;
            var inputTruncated = input.Length > MaxInputLength;

            cards.Add(new FlowTestCardResultDto
            {
                CardId = group.StepId,
                CardName = string.IsNullOrWhiteSpace(last.NodeName) && nodeById.TryGetValue(group.StepId, out var known)
                    ? known.NodeName
                    : last.NodeName,
                Status = group.Steps.Any(step => step.Status == ExecutionStatus.Failed)
                    ? "Failed"
                    : supplied ? "Supplied" : fixture ? "Fixture" : "Passed",
                Order = order++,
                StartedAt = first.StartedAt,
                IsMocked = supplied,
                InvocationCount = group.Steps.Count,
                Input = inputTruncated ? input[..MaxInputLength] : input,
                InputTruncated = inputTruncated,
                Output = last.ResponsePayload ?? string.Empty,
                ErrorMessage = last.ErrorMessage ?? string.Empty,
                HttpStatusCode = last.HttpStatusCode == 0 ? null : last.HttpStatusCode,
                DurationMs = group.Steps.Sum(step => step.CompletedAt.HasValue
                    ? (long)(step.CompletedAt.Value - step.StartedAt).TotalMilliseconds
                    : 0)
            });
        }

        var visited = cards.Select(card => card.CardId).ToHashSet();
        foreach (var node in flowNodes.Where(node => !visited.Contains(node.Id)))
        {
            cards.Add(new FlowTestCardResultDto
            {
                CardId = node.Id,
                CardName = node.NodeName,
                Status = "NotReached",
                Order = order++
            });
        }

        var assertions = definition.Assertions ?? new List<FlowTestAssertion>();
        for (var checkIndex = 0; checkIndex < assertions.Count; checkIndex++)
        {
            var assertion = assertions[checkIndex];
            var candidates = ResolveCandidates(assertion, execution, steps).ToList();
            if (candidates.Count == 0 && IsInvocationCountAssertion(assertion))
                candidates.Add(new AssertionCandidate(null, 0));

            if (candidates.Count == 0)
            {
                AddFailure(checks, failures, assertion, checkIndex, null, null, "Card was not reached.", null);
                continue;
            }

            var selected = SelectInvocations(assertion, candidates).ToList();
            if (selected.Count == 0)
            {
                AddFailure(checks, failures, assertion, checkIndex, null, null,
                    "Requested invocation was not reached.", assertion.InvocationNumber);
                continue;
            }

            if (assertion.InvocationScope.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                var matching = selected.FirstOrDefault(candidate =>
                    CandidatePasses(assertion, candidate, execution.Status.ToString()));
                if (matching == null)
                {
                    AddFailure(checks, failures, assertion, checkIndex, null, selected[0].Step,
                        "No invocation matched the check.", null);
                    continue;
                }

                selected = new[] { matching }.ToList();
            }

            foreach (var candidate in selected)
            {
                var missing = false;
                var actual = assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)
                    ? execution.Status.ToString()
                    : ResolveActual(assertion, candidate.Step, candidate.InvocationCount, out missing);
                var operation = NormalizeOperator(assertion.Operator);
                var passed = operation == "missing"
                    ? missing
                    : operation == "exists"
                        ? !missing
                        : !missing && Matches(actual, assertion.Operator, assertion.ExpectedValue, assertion.Field);
                var message = passed
                    ? string.Empty
                    : missing
                        ? "The selected field is missing."
                        : $"Expected {DescribeOperator(assertion.Operator)} '{FormatValue(assertion.ExpectedValue)}' but was '{FormatValue(actual)}'.";

                var check = new FlowTestCheckResultDto
                {
                    CheckIndex = checkIndex,
                    CardId = assertion.CardId,
                    CardName = candidate.Step?.NodeName ?? assertion.NodeName,
                    Description = Describe(assertion),
                    Status = passed ? "Passed" : "Failed",
                    Field = assertion.Field,
                    Path = assertion.Path,
                    Operator = assertion.Operator,
                    ExpectedValue = assertion.ExpectedValue,
                    ActualValue = actual ?? string.Empty,
                    FailureReason = message,
                    Invocation = candidate.Invocation,
                    IsJsonComparison = operation == "equals"
                        && TryParseJson(actual, out _)
                        && TryParseJson(assertion.ExpectedValue, out _)
                };
                checks.Add(check);
                if (!passed) failures.Add($"{check.Description} {message}");
            }
        }

        foreach (var check in checks.Where(result => result.Status == "Failed"))
        {
            var card = check.CardId.HasValue
                ? cards.FirstOrDefault(candidate => candidate.CardId == check.CardId.Value)
                : cards.FirstOrDefault(candidate =>
                    candidate.CardName.Equals(check.CardName, StringComparison.OrdinalIgnoreCase));
            if (card != null)
            {
                card.FailedCheckCount++;
                if (card.Status is "Passed" or "Fixture" or "Supplied") card.Status = "Failed";
            }
        }

        var expectedRunResult = string.IsNullOrWhiteSpace(definition.ExpectedRunResult)
            ? "Success"
            : definition.ExpectedRunResult;
        var stoppedAt = cards.FirstOrDefault(card => card.Status == "Failed");
        var actualStatus = execution.Status.ToString();
        var runResultPassed = expectedRunResult.Equals("Any", StringComparison.OrdinalIgnoreCase)
            || expectedRunResult.Equals(actualStatus, StringComparison.OrdinalIgnoreCase);
        var expectedLabel = expectedRunResult.Equals("Any", StringComparison.OrdinalIgnoreCase)
            ? "Any"
            : expectedRunResult;
        var expectedCheck = new FlowTestCheckResultDto
        {
            CheckIndex = -1,
            CardId = stoppedAt?.CardId,
            CardName = stoppedAt?.CardName ?? string.Empty,
            Description = $"Run result must be {expectedLabel}",
            Status = runResultPassed ? "Passed" : "Failed",
            Field = "Status",
            Operator = "Equals",
            ExpectedValue = expectedLabel,
            ActualValue = actualStatus,
            FailureReason = runResultPassed
                ? string.Empty
                : $"Expected run result was '{expectedLabel}' but was '{actualStatus}'."
        };
        checks.Add(expectedCheck);
        if (!runResultPassed)
        {
            failures.Add($"{expectedCheck.Description}. {expectedCheck.FailureReason}");
            if (stoppedAt != null) stoppedAt.FailedCheckCount++;
        }

        var failedCard = cards.FirstOrDefault(card => card.Status == "Failed");
        var firstFailedCheck = checks.FirstOrDefault(check => check.Status == "Failed" && check.CheckIndex >= 0);
        if (failedCard == null && firstFailedCheck?.CardId is Guid checkCardId)
            failedCard = cards.FirstOrDefault(card => card.CardId == checkCardId);

        var executionError = execution.ErrorMessage ?? string.Empty;
        var scenarioPassed = failures.Count == 0;
        string headline;
        if (scenarioPassed && execution.Status != ExecutionStatus.Success)
        {
            if (failedCard != null)
            {
                var failureText = failedCard.ErrorMessage;
                if (failedCard.HttpStatusCode is int statusCode && statusCode > 0)
                {
                    var prefix = $"HTTP request failed with status code {statusCode}: ";
                    if (failureText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        failureText = failureText[prefix.Length..];
                    else if (failureText.Equals($"HTTP request failed with status code {statusCode}", StringComparison.OrdinalIgnoreCase))
                        failureText = string.Empty;
                    failureText = string.IsNullOrWhiteSpace(failureText)
                        ? $"HTTP {statusCode}"
                        : $"HTTP {statusCode}: {failureText}";
                }

                headline = string.IsNullOrWhiteSpace(failureText)
                    ? $"Passed: the run failed as expected at \"{failedCard.CardName}\"."
                    : $"Passed: the run failed as expected at \"{failedCard.CardName}\" ({Trim(failureText, 240)})";
            }
            else
            {
                headline = string.IsNullOrWhiteSpace(executionError)
                    ? "Passed: the run failed as expected."
                    : $"Passed: the run failed as expected: {Trim(executionError, 240)}";
            }
        }
        else if (failedCard != null && !string.IsNullOrWhiteSpace(failedCard.ErrorMessage))
        {
            headline = $"Failed at \"{failedCard.CardName}\": {Trim(failedCard.ErrorMessage, 240)}";
        }
        else if (firstFailedCheck != null)
        {
            headline = $"Check failed on \"{firstFailedCheck.CardName}\": {firstFailedCheck.Description}"
                + (string.IsNullOrWhiteSpace(firstFailedCheck.FailureReason) ? "." : $" — {firstFailedCheck.FailureReason}");
        }
        else if (execution.Status != ExecutionStatus.Success)
        {
            headline = $"Expected the run to succeed but it stopped at "
                + $"\"{(string.IsNullOrWhiteSpace(failedCard?.CardName) ? "the flow" : failedCard.CardName)}\": "
                + $"{Trim(executionError, 240)}";
        }
        else if (checks.Count == 0)
        {
            headline = $"All {cards.Count(card => card.Status != "NotReached")} cards completed without checks.";
        }
        else
        {
            headline = $"All {checks.Count(check => check.Status == "Passed")} checks passed across "
                + $"{cards.Count(card => card.Status != "NotReached")} cards.";
        }

        var details = new FlowTestEvaluationDto
        {
            Passed = failures.Count == 0,
            ExecutionStatus = execution.Status.ToString(),
            ExecutionError = executionError,
            FailedCardId = failedCard?.CardId,
            FailedCardName = failedCard?.CardName ?? string.Empty,
            Headline = headline,
            Checks = checks,
            Cards = cards,
            Failures = failures
        };
        return new FlowTestEvaluation(details.Passed, failures, details);
    }

    private sealed record AssertionCandidate(StepExecution? Step, int InvocationCount)
    {
        public int Invocation => Step == null ? 0 : InvocationCount;
    }

    private static IEnumerable<AssertionCandidate> ResolveCandidates(
        FlowTestAssertion assertion,
        FlowExecution execution,
        IReadOnlyList<StepExecution> steps)
    {
        if (assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase))
            return new[] { new AssertionCandidate(null, 1) };

        var matches = steps.Where(step => assertion.CardId.HasValue && assertion.CardId.Value != Guid.Empty
                ? step.StepId == assertion.CardId.Value
                : step.NodeName.Equals(assertion.NodeName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .OrderBy(step => step.StartedAt)
            .ToList();
        return matches.Select((step, index) => new AssertionCandidate(step, index + 1));
    }

    private static bool CandidatePasses(FlowTestAssertion assertion, AssertionCandidate candidate, string executionStatus)
    {
        var missing = false;
        var actual = assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)
            ? executionStatus
            : ResolveActual(assertion, candidate.Step, candidate.InvocationCount, out missing);
        var operation = NormalizeOperator(assertion.Operator);
        return operation == "missing"
            ? missing
            : operation == "exists"
                ? !missing
                : !missing && Matches(actual, assertion.Operator, assertion.ExpectedValue, assertion.Field);
    }

    private static IEnumerable<AssertionCandidate> SelectInvocations(
        FlowTestAssertion assertion,
        IEnumerable<AssertionCandidate> candidates)
    {
        var list = candidates.ToList();
        if (assertion.InvocationScope.Equals("Any", StringComparison.OrdinalIgnoreCase)) return list;
        if (assertion.InvocationScope.Equals("InvocationNumber", StringComparison.OrdinalIgnoreCase))
            return list.Where(item => item.Invocation == assertion.InvocationNumber);
        return list;
    }

    private static string? ResolveActual(
        FlowTestAssertion assertion,
        StepExecution? step,
        int invocationCount,
        out bool missing)
    {
        missing = false;
        if (assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)) return null;
        if (step == null)
        {
            if (IsInvocationCountAssertion(assertion))
                return invocationCount.ToString(CultureInfo.InvariantCulture);
            missing = true;
            return null;
        }

        string? value = assertion.Field.ToLowerInvariant() switch
        {
            "status" or "cardstatus" => step.Status.ToString(),
            "responsepayload" or "output" or "outputfield" => step.ResponsePayload,
            "receivedinput" or "input" => step.ReceivedInput,
            "httpstatus" or "httpstatuscode" => step.HttpStatusCode.ToString(CultureInfo.InvariantCulture),
            "errormessage" or "error" => step.ErrorMessage,
            "invocationcount" or "count" => invocationCount.ToString(CultureInfo.InvariantCulture),
            _ => step.ResponsePayload
        };

        if (!string.IsNullOrWhiteSpace(assertion.Path))
        {
            if (!TrySelect(value, assertion.Path, out value)) missing = true;
        }
        else if (value == null
                 || (string.Equals(assertion.Field, "ResponsePayload", StringComparison.OrdinalIgnoreCase)
                     && string.IsNullOrWhiteSpace(value)))
        {
            missing = true;
        }

        return value;
    }

    private static bool IsInvocationCountAssertion(FlowTestAssertion assertion)
        => assertion.Field.Equals("InvocationCount", StringComparison.OrdinalIgnoreCase)
           || assertion.Field.Equals("Count", StringComparison.OrdinalIgnoreCase);

    private static bool TrySelect(string? json, string path, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var token = JToken.Parse(json).SelectToken(path, false);
            if (token == null) return false;
            value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Matches(string? actual, string? op, string? expected, string field)
    {
        if (actual == null) return false;
        expected ??= string.Empty;
        var operation = NormalizeOperator(op);
        if (operation is "exists") return true;
        if (operation is "missing") return false;
        if (operation is "collectioncount")
        {
            try { return JToken.Parse(actual).Count() == int.Parse(expected, CultureInfo.InvariantCulture); }
            catch { return false; }
        }

        if (operation is "greaterthan" or "greaterorequal" or "lessthan" or "lessorequal")
        {
            if (!decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
                || !decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var e))
                return false;
            return operation switch
            {
                "greaterthan" => a > e,
                "greaterorequal" => a >= e,
                "lessthan" => a < e,
                _ => a <= e
            };
        }

        if (operation == "equals" && TryParseJson(actual, out var actualJson) && TryParseJson(expected, out var expectedJson))
            return JToken.DeepEquals(actualJson, expectedJson);

        return operation switch
        {
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "notcontains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase) == false,
            "startswith" => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "endswith" => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "notequals" or "inequality" => !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            _ => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static void AddFailure(
        List<FlowTestCheckResultDto> checks,
        List<string> failures,
        FlowTestAssertion assertion,
        int checkIndex,
        string? actual,
        StepExecution? step,
        string reason,
        int? invocation)
    {
        var description = Describe(assertion);
        checks.Add(new FlowTestCheckResultDto
        {
            CheckIndex = checkIndex,
            CardId = assertion.CardId,
            CardName = step?.NodeName ?? assertion.NodeName,
            Description = description,
            Status = "Failed",
            Field = assertion.Field,
            Path = assertion.Path,
            Operator = assertion.Operator,
            ExpectedValue = assertion.ExpectedValue,
            ActualValue = actual ?? string.Empty,
            FailureReason = reason,
            Invocation = invocation
        });
        failures.Add($"{description} {reason}");
    }

    private static bool TryParseJson(string? value, out JToken? token)
    {
        try
        {
            token = JToken.Parse(value ?? string.Empty);
            return true;
        }
        catch (JsonException)
        {
            token = null;
            return false;
        }
    }

    private static string Describe(FlowTestAssertion assertion)
    {
        if (assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase))
            return $"Run status {DescribeOperator(assertion.Operator)} \"{assertion.ExpectedValue}\"";

        var cardName = string.IsNullOrWhiteSpace(assertion.NodeName) ? "Card" : assertion.NodeName;
        var field = assertion.Field.ToLowerInvariant() switch
        {
            "responsepayload" or "output" or "outputfield" => "output",
            "receivedinput" or "input" => "input",
            "status" or "cardstatus" => "status",
            "httpstatus" or "httpstatuscode" => "http status",
            "errormessage" or "error" => "error",
            "invocationcount" or "count" => "invocation count",
            _ => assertion.Field
        };
        if (!string.IsNullOrWhiteSpace(assertion.Path)) field += $".{assertion.Path}";
        return $"{cardName} → {field} {DescribeOperator(assertion.Operator)} \"{FormatValue(assertion.ExpectedValue)}\"";
    }

    private static string NormalizeOperator(string? op)
        => op?.Replace(" ", string.Empty).ToLowerInvariant() ?? "equals";

    private static string DescribeOperator(string? op) => NormalizeOperator(op) switch
    {
        "equals" => "must equal",
        "notequals" or "inequality" => "must not equal",
        "contains" => "must contain",
        "notcontains" => "must not contain",
        "startswith" => "must start with",
        "endswith" => "must end with",
        "exists" => "must exist",
        "missing" => "must be missing",
        "collectioncount" => "must have a count of",
        "greaterthan" => "must be greater than",
        "greaterorequal" => "must be greater than or equal to",
        "lessthan" => "must be less than",
        "lessorequal" => "must be less than or equal to",
        _ => $"must satisfy {op}"
    };

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Length > 320 ? value[..320] + "…" : value;

    private static string Trim(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length > length ? value[..length] + "…" : value;
}
