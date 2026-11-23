using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Blazor.Diagrams.Core.PathGenerators;
using SvgPathProperties;

namespace SimpleIPaaS.Client.Services;

/// <summary>
/// Orthogonal, direction-aware link routing. Right outputs leave right, bottom outputs leave down,
/// and left inputs are approached from the left, avoiding the long loops produced by the library's
/// default 125px smooth control points.
/// </summary>
public sealed class DirectionalPathGenerator : PathGenerator
{
    private const double Stub = 16d;
    private const double CornerRadius = 10d;

    public override PathGeneratorResult GetResult(
        Diagram diagram,
        BaseLinkModel link,
        Point[] route,
        Point source,
        Point target)
    {
        if (!TryGetAlignment(link.Source, out var sourceAlignment)
            || !TryGetAlignment(link.Target, out var targetAlignment))
        {
            var path = new SvgPath().AddMoveTo(source.X, source.Y).AddLineTo(target.X, target.Y);
            return new PathGeneratorResult(path, Array.Empty<SvgPath>(), null, source, null, target);
        }

        var points = BuildRoute(source, sourceAlignment, target, targetAlignment);
        if (points == null)
        {
            var margin = 40d;
            var control = new SvgPath()
                .AddMoveTo(source.X, source.Y)
                .AddCubicBezierCurve(
                    source.X + margin, source.Y,
                    target.X - margin, target.Y,
                    target.X, target.Y);
            return new PathGeneratorResult(control, Array.Empty<SvgPath>(), null, source, null, target);
        }

        return new PathGeneratorResult(
            BuildRoundedPath(points),
            Array.Empty<SvgPath>(),
            null,
            source,
            null,
            target);
    }

    public static Point[]? BuildRoute(
        Point source,
        PortAlignment sourceAlignment,
        Point target,
        PortAlignment targetAlignment)
    {
        if (sourceAlignment == PortAlignment.Right && targetAlignment == PortAlignment.Left)
        {
            var gap = target.X - source.X;
            var verticalX = gap >= 2 * Stub
                ? source.X + (gap / 2)
                : source.X + Math.Max(4d, gap / 2);
            return new[]
            {
                source,
                new Point(verticalX, source.Y),
                new Point(verticalX, target.Y),
                target
            };
        }

        if (sourceAlignment is PortAlignment.Bottom or PortAlignment.Top
            && targetAlignment == PortAlignment.Left)
        {
            var dropY = sourceAlignment == PortAlignment.Bottom ? source.Y + Stub : source.Y - Stub;
            var entryX = target.X - Stub;
            return new[]
            {
                source,
                new Point(source.X, dropY),
                new Point(entryX, dropY),
                new Point(entryX, target.Y),
                target
            };
        }

        return null;
    }

    private static bool TryGetAlignment(Anchor anchor, out PortAlignment alignment)
    {
        if (anchor is SinglePortAnchor { Port: { } port })
        {
            alignment = port.Alignment;
            return true;
        }

        alignment = default;
        return false;
    }

    private static SvgPath BuildRoundedPath(IReadOnlyList<Point> points)
    {
        var path = new SvgPath().AddMoveTo(points[0].X, points[0].Y);
        for (var i = 1; i < points.Count - 1; i++)
        {
            var previous = points[i - 1];
            var corner = points[i];
            var next = points[i + 1];
            var radius = Math.Min(
                CornerRadius,
                Math.Min(Distance(previous, corner), Distance(corner, next)) / 2);
            var before = MoveToward(corner, previous, radius);
            var after = MoveToward(corner, next, radius);
            path.AddLineTo(before.X, before.Y);
            path.AddCubicBezierCurve(corner.X, corner.Y, corner.X, corner.Y, after.X, after.Y);
        }

        var last = points[^1];
        path.AddLineTo(last.X, last.Y);
        return path;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Point MoveToward(Point from, Point to, double distance)
    {
        var length = Distance(from, to);
        if (length <= 0) return from;
        var ratio = Math.Min(1d, distance / length);
        return new Point(
            from.X + ((to.X - from.X) * ratio),
            from.Y + ((to.Y - from.Y) * ratio));
    }
}
