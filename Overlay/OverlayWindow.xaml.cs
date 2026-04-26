using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FoxSense.Overlay;

/// <summary>
/// High-performance transparent overlay using DrawingVisual.
/// Click-through via WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW.
/// </summary>
public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly DrawingVisual _visual = new();

    public OverlayWindow()
    {
        InitializeComponent();
        AddVisualChild(_visual);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Returns a DrawingContext for one frame. Caller must dispose it.
    /// </summary>
    public DrawingContext BeginFrame()
    {
        return _visual.RenderOpen();
    }

    // Required overrides for hosting a DrawingVisual
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;
}
