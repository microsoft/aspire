// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// A rectangle in device-independent pixels with a top-left origin and y growing down,
/// which is the coordinate system of the shared panel on every platform.
/// </summary>
internal readonly record struct PanelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool Contains(PanelRect other) => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool IntersectsWith(PanelRect other) => other.X < Right && X < other.Right && other.Y < Bottom && Y < other.Bottom;

    public PanelRect Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public PanelRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    public PanelRect Intersect(PanelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= x || bottom <= y ? default : new(x, y, right - x, bottom - y);
    }
}

internal enum PanelPathVerb
{
    MoveTo,
    LineTo,
    CubicTo,
    Close
}

/// <summary>
/// One path segment. MoveTo and LineTo use (X3, Y3) as the end point so every verb
/// keeps its end point in the same fields; CubicTo also uses both control points.
/// </summary>
internal readonly record struct PanelPathSegment(PanelPathVerb Verb, double X1, double Y1, double X2, double Y2, double X3, double Y3);

/// <summary>
/// A vector path limited to the primitives that AppKit (NSBezierPath), GDI+ and SVG all
/// draw identically: move, line, cubic Bézier and close. Rounded rectangles, circles and
/// arcs are approximated with cubics here so platform renderers never need their own
/// shape helpers and every glyph comes out the same on every OS.
/// </summary>
internal sealed class PanelPath
{
    // Distance of the control points for a 90° arc, 4/3 * tan(π/8).
    // See https://spencermortensen.com/articles/bezier-circle/.
    internal const double QuarterArcKappa = 0.5522847498;

    private readonly List<PanelPathSegment> _segments = [];

    public IReadOnlyList<PanelPathSegment> Segments => _segments;

    public PanelPath MoveTo(double x, double y)
    {
        _segments.Add(new(PanelPathVerb.MoveTo, 0, 0, 0, 0, x, y));
        return this;
    }

    public PanelPath LineTo(double x, double y)
    {
        _segments.Add(new(PanelPathVerb.LineTo, 0, 0, 0, 0, x, y));
        return this;
    }

    public PanelPath CubicTo(double x1, double y1, double x2, double y2, double x, double y)
    {
        _segments.Add(new(PanelPathVerb.CubicTo, x1, y1, x2, y2, x, y));
        return this;
    }

    public PanelPath Close()
    {
        _segments.Add(new(PanelPathVerb.Close, 0, 0, 0, 0, 0, 0));
        return this;
    }

    /// <summary>
    /// Appends a circular arc. Angles are in degrees on the y-down panel axes, so 0° points
    /// right, 90° points down and a positive sweep turns clockwise on screen. The arc is
    /// split into segments of 90° or less, which keeps the cubic error below 0.03% of the radius.
    /// </summary>
    public PanelPath Arc(double centerX, double centerY, double radius, double startDegrees, double sweepDegrees, bool connect = false)
    {
        var start = startDegrees * Math.PI / 180;
        var sweep = sweepDegrees * Math.PI / 180;
        var x0 = centerX + radius * Math.Cos(start);
        var y0 = centerY + radius * Math.Sin(start);
        if (connect)
        {
            LineTo(x0, y0);
        }
        else
        {
            MoveTo(x0, y0);
        }
        var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
        var step = sweep / count;
        // The tangent length for an arc of angle φ is 4/3 * tan(φ/4) times the radius.
        // A negative step gives a negative factor, which turns the tangents around too.
        var k = 4.0 / 3.0 * Math.Tan(step / 4) * radius;
        for (var i = 0; i < count; i++)
        {
            var a0 = start + step * i;
            var a1 = a0 + step;
            var (s0, c0) = Math.SinCos(a0);
            var (s1, c1) = Math.SinCos(a1);
            CubicTo(
                centerX + radius * c0 - k * s0, centerY + radius * s0 + k * c0,
                centerX + radius * c1 + k * s1, centerY + radius * s1 - k * c1,
                centerX + radius * c1, centerY + radius * s1);
        }
        return this;
    }

    public static PanelPath Circle(double centerX, double centerY, double radius)
        => new PanelPath().Arc(centerX, centerY, radius, 0, 360).Close();

    public static PanelPath Rectangle(PanelRect rect)
        => new PanelPath().MoveTo(rect.X, rect.Y).LineTo(rect.Right, rect.Y).LineTo(rect.Right, rect.Bottom)
            .LineTo(rect.X, rect.Bottom).Close();

    public static PanelPath RoundedRect(PanelRect rect, double radius)
        => RoundedRect(rect, radius, radius, radius, radius);

    /// <summary>Builds a rounded rectangle with independent corner radii, clockwise from the top-left corner.</summary>
    public static PanelPath RoundedRect(PanelRect rect, double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        var limit = Math.Min(rect.Width, rect.Height) / 2;
        topLeft = Math.Clamp(topLeft, 0, limit);
        topRight = Math.Clamp(topRight, 0, limit);
        bottomRight = Math.Clamp(bottomRight, 0, limit);
        bottomLeft = Math.Clamp(bottomLeft, 0, limit);
        var path = new PanelPath().MoveTo(rect.X + topLeft, rect.Y).LineTo(rect.Right - topRight, rect.Y);
        if (topRight > 0)
        {
            path.Arc(rect.Right - topRight, rect.Y + topRight, topRight, -90, 90, connect: true);
        }
        path.LineTo(rect.Right, rect.Bottom - bottomRight);
        if (bottomRight > 0)
        {
            path.Arc(rect.Right - bottomRight, rect.Bottom - bottomRight, bottomRight, 0, 90, connect: true);
        }
        path.LineTo(rect.X + bottomLeft, rect.Bottom);
        if (bottomLeft > 0)
        {
            path.Arc(rect.X + bottomLeft, rect.Bottom - bottomLeft, bottomLeft, 90, 90, connect: true);
        }
        path.LineTo(rect.X, rect.Y + topLeft);
        if (topLeft > 0)
        {
            path.Arc(rect.X + topLeft, rect.Y + topLeft, topLeft, 180, 90, connect: true);
        }
        return path.Close();
    }

    /// <summary>Returns a copy scaled by <paramref name="scale"/> and then moved by the offset.</summary>
    public PanelPath Transform(double scale, double offsetX, double offsetY)
    {
        var result = new PanelPath();
        foreach (var s in _segments)
        {
            result._segments.Add(s.Verb == PanelPathVerb.Close
                ? s
                : new(s.Verb,
                    s.X1 * scale + offsetX, s.Y1 * scale + offsetY,
                    s.X2 * scale + offsetX, s.Y2 * scale + offsetY,
                    s.X3 * scale + offsetX, s.Y3 * scale + offsetY));
        }
        return result;
    }

    /// <summary>Returns the bounds of every end and control point, which contain the drawn curve.</summary>
    public PanelRect GetControlBounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in _segments)
        {
            switch (s.Verb)
            {
                case PanelPathVerb.CubicTo:
                    Include(s.X1, s.Y1);
                    Include(s.X2, s.Y2);
                    Include(s.X3, s.Y3);
                    break;
                case PanelPathVerb.MoveTo or PanelPathVerb.LineTo:
                    Include(s.X3, s.Y3);
                    break;
            }
        }
        return minX > maxX ? default : new(minX, minY, maxX - minX, maxY - minY);

        void Include(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
    }
}
