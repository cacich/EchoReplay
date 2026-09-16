using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoReplay;

public sealed class WaveformView : FrameworkElement
{
    public float[] Peaks { get; set; } = [];
    public double Duration { get; set; }
    public double SelectionStart { get; private set; }
    public double SelectionEnd { get; private set; }
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; }
    public double Playhead { get; set; }
    private double anchor;
    public event Action? SelectionChanged;
    public WaveformView() { Cursor = Cursors.Cross; Focusable = true; ClipToBounds = true; }
    public void SetSelection(double a, double b)
    {
        SelectionStart = Math.Clamp(Math.Min(a, b), 0, Duration); SelectionEnd = Math.Clamp(Math.Max(a, b), 0, Duration);
        InvalidateVisual(); SelectionChanged?.Invoke();
    }
    public void Fit() { ViewStart = 0; ViewEnd = Duration; InvalidateVisual(); }
    public void ZoomSelection()
    {
        if (SelectionEnd <= SelectionStart) return;
        ViewStart = SelectionStart; ViewEnd = SelectionEnd; InvalidateVisual();
    }
    public void Pan(double fraction)
    {
        double width = ViewEnd - ViewStart;
        ViewStart = Math.Clamp(ViewStart + width * fraction, 0, Math.Max(0, Duration - width)); ViewEnd = ViewStart + width; InvalidateVisual();
    }
    private double TimeAt(double x) => Math.Clamp(ViewStart + x / Math.Max(1, ActualWidth) * (ViewEnd - ViewStart), 0, Duration);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); anchor = TimeAt(e.GetPosition(this).X); CaptureMouse(); SetSelection(anchor, anchor); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) SetSelection(anchor, TimeAt(e.GetPosition(this).X)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) { SetSelection(anchor, TimeAt(e.GetPosition(this).X)); ReleaseMouseCapture(); } }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Duration <= 0) return;
        double center = TimeAt(e.GetPosition(this).X), fraction = e.GetPosition(this).X / Math.Max(1, ActualWidth);
        double width = Math.Clamp((ViewEnd - ViewStart) * (e.Delta > 0 ? 0.7 : 1.4), Math.Min(0.05, Duration), Duration);
        ViewStart = Math.Clamp(center - fraction * width, 0, Duration - width); ViewEnd = ViewStart + width; InvalidateVisual(); e.Handled = true;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(16, 25, 28)), null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        double width = ViewEnd - ViewStart;
        if (width <= 0 || Duration <= 0) return;
        double X(double time) => (time - ViewStart) / width * ActualWidth;
        double left = Math.Clamp(X(SelectionStart), 0, ActualWidth), right = Math.Clamp(X(SelectionEnd), 0, ActualWidth);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 136, 227, 196)), null, new Rect(left, 0, Math.Max(0, right - left), ActualHeight));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(136, 227, 196)), 1);
        double mid = (ActualHeight - 24) / 2;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(52, 69, 74)), 1), new Point(0, mid), new Point(ActualWidth, mid));
        for (int x = 0; x < (int)ActualWidth; x++)
        {
            int from = Math.Clamp((int)(TimeAt(x) * 200), 0, Peaks.Length);
            int to = Math.Clamp((int)Math.Ceiling(TimeAt(x + 1) * 200), from, Peaks.Length);
            float peak = 0;
            for (int i = from; i < to; i++) peak = Math.Max(peak, Peaks[i]);
            double height = peak * (mid - 8);
            dc.DrawLine(pen, new Point(x, mid - height), new Point(x, mid + height));
        }
        for (int i = 0; i <= 4; i++)
        {
            string label = (ViewStart + width * i / 4).ToString("0.00", CultureInfo.CurrentCulture) + "s";
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Clamp(ActualWidth * i / 4 - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width)), ActualHeight - 20));
        }
        double playX = X(Playhead);
        if (playX >= 0 && playX <= ActualWidth) dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(playX, 0), new Point(playX, ActualHeight - 24));
    }
}
