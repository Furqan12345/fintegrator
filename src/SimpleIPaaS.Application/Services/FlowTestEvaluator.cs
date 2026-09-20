using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SimpleIPaaS.Application.Models;
using SimpleIPaaS.Domain;
using SimpleIPaaS.Domain.Entities;


namespace SimpleIPaaS.Application.Services;

public sealed class FlowTestEvaluationDto { public bool Passed { get; set; } public List<FlowTestCheckResultDto> Checks { get; set; } = new(); public List<FlowTestCardResultDto> Cards { get; set; } = new(); public List<string> Failures { get; set; } = new(); }
public sealed class FlowTestCheckResultDto { public Guid? CardId { get; set; } public string CardName { get; set; } = string.Empty; public string Status { get; set; } = string.Empty; public string ExpectedValue { get; set; } = string.Empty; public string ActualValue { get; set; } = string.Empty; public string FailureReason { get; set; } = string.Empty; public int? Invocation { get; set; } }
public sealed class FlowTestCardResultDto { public Guid CardId { get; set; } public string CardName { get; set; } = string.Empty; public string Status { get; set; } = "Not reached"; public bool IsMocked { get; set; } public int InvocationCount { get; set; } public string Output { get; set; } = string.Empty; public string ErrorMessage { get; set; } = string.Empty; public long DurationMs { get; set; } }


public sealed record FlowTestEvaluation(bool Passed, IReadOnlyList<string> Failures, FlowTestEvaluationDto Details)
{
    public string ToJson() => JsonConvert.SerializeObject(Details);
}

public static class FlowTestEvaluator
{
    public static FlowTestEvaluation Evaluate(FlowExecution execution, IEnumerable<StepExecution> stepSequence, FlowTestDefinition definition)
    {
        var steps = stepSequence.ToList();
        var failures = new List<string>();
        var checks = new List<FlowTestCheckResultDto>();
        var cards = steps.GroupBy(step => step.StepId).Select(group =>
        {
            var last = group.OrderBy(step => step.StartedAt).Last();
            var mocked = (definition.CardOverrides ?? new()).Any(item => item.CardId == group.Key && item.Mode.Equals("Supply", StringComparison.OrdinalIgnoreCase))
                || (definition.HttpResponses ?? new()).Keys.Any(key => Guid.TryParse(key, out var id) && id == group.Key);
            return new FlowTestCardResultDto
            {
                CardId = group.Key,
                CardName = last.NodeName,
                Status = group.Any(step => step.Status == ExecutionStatus.Failed) ? "Failed" : "Passed",
                IsMocked = mocked,
                InvocationCount = group.Count(),
                Output = group.OrderBy(step => step.StartedAt).Last().ResponsePayload ?? string.Empty,
                ErrorMessage = last.ErrorMessage ?? string.Empty,
                DurationMs = group.Sum(step => step.CompletedAt.HasValue ? (long)(step.CompletedAt.Value - step.StartedAt).TotalMilliseconds : 0)
            };
        }).ToList();

        foreach (var assertion in definition.Assertions ?? new())
        {
            var candidates = ResolveCandidates(assertion, execution, steps).ToList();
            if (candidates.Count == 0 && IsInvocationCountAssertion(assertion))
                candidates.Add(new AssertionCandidate(null, 0));

            if (candidates.Count == 0)
            {
                AddFailure(checks, failures, assertion, null, null, "Card was not reached.", null);
                continue;
            }

            var selected = SelectInvocations(assertion, candidates).ToList();
            if (selected.Count == 0)
            {
                AddFailure(checks, failures, assertion, null, null, "Requested invocation was not reached.", assertion.InvocationNumber);
                continue;
            }

            if (assertion.InvocationScope.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                var matching = selected.FirstOrDefault(candidate => CandidatePasses(assertion, candidate, execution.Status.ToString()));
                if (matching == null)
                {
                    AddFailure(checks, failures, assertion, null, selected[0].Step, "No invocation matched the check.", null);
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
                var operation = assertion.Operator?.Replace(" ", string.Empty).ToLowerInvariant();
                var passed = operation == "missing" ? missing : operation == "exists" ? !missing : !missing && Matches(actual, assertion.Operator, assertion.ExpectedValue, assertion.Field);
                var message = passed ? string.Empty : missing ? "The selected field is missing." : $"Expected {DescribeOperator(assertion.Operator)} '{FormatValue(assertion.ExpectedValue)}' but was '{FormatValue(actual)}'.";
                var result = new FlowTestCheckResultDto
                {
                    CardId = assertion.CardId,
                    CardName = candidate.Step?.NodeName ?? assertion.NodeName,
                    Status = passed ? "Passed" : "Failed",
                    ExpectedValue = assertion.ExpectedValue,
                    ActualValue = actual ?? string.Empty,
                    FailureReason = message,
                    Invocation = candidate.Invocation
                };
                checks.Add(result);
                if (!passed) failures.Add($"{Describe(assertion, candidate.Step)} {message}");
            }
        }

        var explicitlyExpectedFailure = (definition.Assertions ?? new()).Any(assertion =>
            assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)
            && assertion.ExpectedValue.Equals(execution.Status.ToString(), StringComparison.OrdinalIgnoreCase));
        if (execution.Status != ExecutionStatus.Success && !explicitlyExpectedFailure)
            failures.Add($"Execution status was {execution.Status}: {execution.ErrorMessage ?? "no error message"}.");

        var details = new FlowTestEvaluationDto { Passed = failures.Count == 0, Checks = checks, Cards = cards, Failures = failures };
        return new FlowTestEvaluation(details.Passed, failures, details);
    }

    private sealed record AssertionCandidate(StepExecution? Step, int InvocationCount)
    {
        public int Invocation => Step == null ? 0 : InvocationCount;
    }

    private static IEnumerable<AssertionCandidate> ResolveCandidates(FlowTestAssertion assertion, FlowExecution execution, IReadOnlyList<StepExecution> steps)
    {
        if (assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase))
            return new[] { new AssertionCandidate(null, 1) };

        var matches = steps.Where(step => assertion.CardId.HasValue && assertion.CardId.Value != Guid.Empty
                ? step.StepId == assertion.CardId.Value
                : step.NodeName.Equals(assertion.NodeName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            .OrderBy(step => step.StartedAt)
            .ToList();
        var count = matches.Count;
        return matches.Select((step, index) => new AssertionCandidate(step, index + 1));
    }

    private static bool CandidatePasses(FlowTestAssertion assertion, AssertionCandidate candidate, string executionStatus)
    {
        var missing = false;
        var actual = assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)
            ? executionStatus
            : ResolveActual(assertion, candidate.Step, candidate.InvocationCount, out missing);
        var operation = assertion.Operator?.Replace(" ", string.Empty).ToLowerInvariant();
        return operation == "missing" ? missing : operation == "exists" ? !missing : !missing && Matches(actual, assertion.Operator, assertion.ExpectedValue, assertion.Field);
    }
    private static IEnumerable<AssertionCandidate> SelectInvocations(FlowTestAssertion assertion, IEnumerable<AssertionCandidate> candidates)
    {
        var list = candidates.ToList();
        if (assertion.InvocationScope.Equals("Any", StringComparison.OrdinalIgnoreCase)) return list;
        if (assertion.InvocationScope.Equals("InvocationNumber", StringComparison.OrdinalIgnoreCase))
            return list.Where(item => item.Invocation == assertion.InvocationNumber);
        return list;
    }

    private static string? ResolveActual(FlowTestAssertion assertion, StepExecution? step, int invocationCount, out bool missing)
    {
        missing = false;
        if (assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase)) return null;
        if (step == null)
        {
            if (IsInvocationCountAssertion(assertion)) return invocationCount.ToString(CultureInfo.InvariantCulture);
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
        else if (value == null || (string.Equals(assertion.Field, "ResponsePayload", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(value)))
        {
            missing = true;
        }
        return value;
    }

    private static bool IsInvocationCountAssertion(FlowTestAssertion assertion)
        => assertion.Field.Equals("InvocationCount", StringComparison.OrdinalIgnoreCase) || assertion.Field.Equals("Count", StringComparison.OrdinalIgnoreCase);

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
        catch (JsonException) { return false; }
    }

    private static bool Matches(string? actual, string? op, string? expected, string field)
    {
        if (actual == null) return false;
        expected ??= string.Empty;
        var operation = op?.Replace(" ", string.Empty).ToLowerInvariant() ?? "equals";
        if (operation is "exists") return true;
        if (operation is "missing") return false;
        if (operation is "collectioncount")
        {
            try { return JToken.Parse(actual).Count() == int.Parse(expected, CultureInfo.InvariantCulture); }
            catch { return false; }
        }
        if (operation is "greaterthan" or "greaterorequal" or "lessthan" or "lessorequal")
        {
            if (!decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) || !decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var e)) return false;
            return operation switch { "greaterthan" => a > e, "greaterorequal" => a >= e, "lessthan" => a < e, _ => a <= e };
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

    private static void AddFailure(List<FlowTestCheckResultDto> checks, List<string> failures, FlowTestAssertion assertion, string? actual, StepExecution? step, string reason, int? invocation)
    {
        checks.Add(new FlowTestCheckResultDto { CardId = assertion.CardId, CardName = step?.NodeName ?? assertion.NodeName, Status = "Failed", ExpectedValue = assertion.ExpectedValue, ActualValue = actual ?? string.Empty, FailureReason = reason, Invocation = invocation });
        failures.Add($"{Describe(assertion, step)} {reason}");
    }

    private static bool TryParseJson(string value, out JToken? token)
    {
        try { token = JToken.Parse(value); return true; } catch (JsonException) { token = null; return false; }
    }

    private static string Describe(FlowTestAssertion assertion, StepExecution? step)
        => assertion.Type.Equals("ExecutionStatus", StringComparison.OrdinalIgnoreCase) ? "Execution status" : $"{step?.NodeName ?? assertion.NodeName} → {assertion.Field}{(string.IsNullOrWhiteSpace(assertion.Path) ? string.Empty : $" ({assertion.Path})")}";

    private static string DescribeOperator(string? op) => op?.Replace("NotContains", "must not contain").Replace("Contains", "must contain").Replace("Equals", "must equal") ?? "must equal";
    private static string FormatValue(string? value) => string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Length > 320 ? value[..320] + "…" : value;
}