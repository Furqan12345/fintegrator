using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SimpleIPaaS.Infrastructure.Services;

// C# scripts require using directives before any declaration, so the flow helper and the
// node script are merged with all leading usings hoisted to the top. Helper declarations
// and the node script body keep their own order.
public static class ScriptComposer
{
    public static string Compose(string? helperCode, string script)
    {
        if (string.IsNullOrWhiteSpace(helperCode))
        {
            return script;
        }

        var (helperUsings, helperBody) = SplitLeadingUsings(helperCode);
        var (scriptUsings, scriptBody) = SplitLeadingUsings(script);
        var usings = helperUsings
            .Concat(scriptUsings)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        foreach (var directive in usings)
        {
            builder.AppendLine(directive);
        }

        if (usings.Count > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine(helperBody.Trim());
        builder.AppendLine();
        builder.Append(scriptBody.TrimStart());
        return builder.ToString();
    }

    private static (List<string> Usings, string Body) SplitLeadingUsings(string code)
    {
        var normalized = code.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var usings = new List<string>();
        var body = new List<string>();
        var inHeader = true;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (inHeader && (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)))
            {
                body.Add(line);
                continue;
            }

            if (inHeader &&
                trimmed.EndsWith(";", StringComparison.Ordinal) &&
                (trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                 trimmed.StartsWith("global using ", StringComparison.Ordinal)))
            {
                usings.Add(trimmed);
                continue;
            }

            inHeader = false;
            body.Add(line);
        }

        return (usings, string.Join("\n", body));
    }
}
