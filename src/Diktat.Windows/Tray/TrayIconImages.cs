using System.Drawing.Drawing2D;
using Diktat.Windows.Runtime;
using WpfApplication = System.Windows.Application;

namespace Diktat.Windows.Tray;

/// The tray symbols: the Diktat icon, and the same with a small dot while recording (red),
/// processing (lilac) or a meeting runs (red), or with a warning sign while a meeting recording
/// has a problem.
internal sealed class TrayIconImages : IDisposable
{
    private static readonly Color RecordingDot = Color.FromArgb(0xD9, 0x4C, 0x76);
    private static readonly Color ProcessingDot = Color.FromArgb(0xBB, 0x84, 0xDF);
    private static readonly Color WarningFill = Color.FromArgb(0xF2, 0xA9, 0x00);

    public TrayIconImages()
    {
        Idle = LoadAppIcon();
        Recording = WithDot(Idle, RecordingDot);
        Processing = WithDot(Idle, ProcessingDot);
        Warning = WithWarningSign(Idle);
    }

    public Icon Idle { get; }
    public Icon Recording { get; }
    public Icon Processing { get; }
    public Icon Warning { get; }

    private static Icon LoadAppIcon()
    {
        var size = SystemInformation.SmallIconSize;
        try
        {
            var resource = WpfApplication.GetResourceStream(new Uri("pack://application:,,,/Assets/Diktat.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, size);
            }
        }
        catch (Exception ex)
        {
            Platform.Log.Warn("Loading the tray icon failed", ex);
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    private static Icon WithDot(Icon icon, Color color) => Draw(icon, (graphics, width, height) =>
    {
        float diameter = width * 0.46f;
        var rect = new RectangleF(width - diameter - 0.5f, height - diameter - 0.5f, diameter, diameter);
        using var outline = new SolidBrush(Color.White);
        graphics.FillEllipse(outline, RectangleF.Inflate(rect, 1.2f, 1.2f));
        using var fill = new SolidBrush(color);
        graphics.FillEllipse(fill, rect);
    });

    /// An amber triangle with "!" in the lower right corner, the size of the dot and larger.
    private static Icon WithWarningSign(Icon icon) => Draw(icon, (graphics, width, height) =>
    {
        float size = width * 0.62f;
        float left = width - size - 0.5f, top = height - size - 0.5f;
        PointF[] triangle = [new(left + size / 2, top), new(left + size, top + size), new(left, top + size)];
        using (var outline = new Pen(Color.White, 2.4f) { LineJoin = LineJoin.Round })
        {
            graphics.DrawPolygon(outline, triangle);
        }
        using (var fill = new SolidBrush(WarningFill))
        {
            graphics.FillPolygon(fill, triangle);
        }
        float x = left + size / 2;
        using var mark = new Pen(Color.Black, Math.Max(1.2f, size * 0.13f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(mark, x, top + size * 0.36f, x, top + size * 0.66f);
        float dot = Math.Max(1.2f, size * 0.13f);
        using var dotBrush = new SolidBrush(Color.Black);
        graphics.FillEllipse(dotBrush, x - dot / 2, top + size * 0.76f, dot, dot);
    });

    private static Icon Draw(Icon icon, Action<Graphics, int, int> paint)
    {
        using var bitmap = icon.ToBitmap();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            paint(graphics, bitmap.Width, bitmap.Height);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            RuntimeNative.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        Idle.Dispose();
        Recording.Dispose();
        Processing.Dispose();
        Warning.Dispose();
    }
}
