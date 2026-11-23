using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SimpleIPaaS.Infrastructure.Services;

// Roslyn cannot abort a running script. A script timeout used to only stop *waiting*: a script stuck in
// `while (true) { }` kept a thread-pool thread spinning at 100% of a core until the process restarted, and every
// further runaway run added another one.
//
// Before compiling, every loop body and every goto gets a cooperative cancellation check against the token the
// script runner cancels at the time limit, so a runaway loop throws instead of spinning. The check is inserted
// on the line it guards, so line numbers in compile errors are unchanged. What it cannot stop: a single framework
// call that never returns (for example an endless LINQ sequence) and unbounded recursion.
public static class ScriptLoopGuard
{
    // A globals member every script type exposes (see AdvancedScriptGlobals / ScriptGlobals).
    public const string TokenMember = "__FusetorCancellation";

    private static readonly CSharpParseOptions ScriptParse = new(kind: SourceCodeKind.Script);

    public static string Instrument(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        var tree = CSharpSyntaxTree.ParseText(code, ScriptParse);
        var rewritten = new Rewriter().Visit(tree.GetRoot());
        return rewritten.ToFullString();
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        private static StatementSyntax Check() =>
            SyntaxFactory.ParseStatement($"{TokenMember}.ThrowIfCancellationRequested();");

        private static StatementSyntax Guard(StatementSyntax body) =>
            body is BlockSyntax block
                ? block.WithStatements(block.Statements.Insert(0, Check()))
                : SyntaxFactory.Block(Check(), body).WithTriviaFrom(body);

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitGotoStatement(GotoStatementSyntax node)
        {
            var visited = (GotoStatementSyntax)base.VisitGotoStatement(node)!;
            return SyntaxFactory.Block(Check(), visited.WithoutTrivia()).WithTriviaFrom(visited);
        }
    }
}
