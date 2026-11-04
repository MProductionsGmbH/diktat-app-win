using System.Drawing.Drawing2D;
using Diktat.Windows.Runtime;
using WpfApplication = System.Windows.Application;

namespace Diktat.Windows.Tray;

/// The tray symbols: the Diktat icon, and the same with a small dot while recording (red),
/// processing (lilac) or a meeting runs (red).
internal sealed class TrayIconImages : IDisposable
{
    private static readonly Color RecordingDot = Color.FromArgb(0xD9, 0x4C, 0x76);
    private static readonly Color ProcessingDot = Color.FromArgb(0xBB, 0x84, 0xDF);

    public TrayIconImages()
    {
        Idle = LoadAppIcon();
        Recording = WithDot(Idle, RecordingDot);
        Processing = WithDot(Idle, ProcessingDot);
    }

    public Icon Idle { get; }
    public Icon Recording { get; }
    public Icon Processing { get; }

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

    private static Icon WithDot(Icon icon, Color color)
    {
        using var bitmap = icon.ToBitmap();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float diameter = bitmap.Width * 0.46f;
            var rect = new RectangleF(bitmap.Width - diameter - 0.5f, bitmap.Height - diameter - 0.5f, diameter, diameter);
            using var outline = new SolidBrush(Color.White);
            graphics.FillEllipse(outline, RectangleF.Inflate(rect, 1.2f, 1.2f));
            using var fill = new SolidBrush(color);
            graphics.FillEllipse(fill, rect);
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
    }
}
