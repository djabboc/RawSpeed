using System.Windows;

namespace RawSpeed.App.Controls;

/// <summary>Normalized crop gestures, independent of display scale and mouse-device state.</summary>
public static class CropInteraction
{
    // Handles run clockwise from top-left, then 8 = move, 9 = draw a new rectangle.
    public static Rect Drag(Rect original, int handle, Point start, Point current)
    {
        if (handle is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(handle));
        double dx = current.X - start.X, dy = current.Y - start.Y;
        double left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
        double minWidth = Math.Min(0.005, original.Width), minHeight = Math.Min(0.005, original.Height);
        if (handle == 8)
            return new Rect(Math.Clamp(left + dx, 0, 1 - original.Width), Math.Clamp(top + dy, 0, 1 - original.Height), original.Width, original.Height);
        if (handle == 9)
            return new Rect(new Point(Math.Clamp(start.X, 0, 1), Math.Clamp(start.Y, 0, 1)),
                new Point(Math.Clamp(current.X, 0, 1), Math.Clamp(current.Y, 0, 1)));
        if (handle is 0 or 6 or 7) left = Math.Clamp(left + dx, 0, right - minWidth);
        if (handle is 2 or 3 or 4) right = Math.Clamp(right + dx, left + minWidth, 1);
        if (handle is 0 or 1 or 2) top = Math.Clamp(top + dy, 0, bottom - minHeight);
        if (handle is 4 or 5 or 6) bottom = Math.Clamp(bottom + dy, top + minHeight, 1);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }
}
