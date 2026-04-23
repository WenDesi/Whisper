using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace WhisperDesk.Views;

public partial class SettingsDialog : UserControl
{
    public SettingsDialog()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Converts a volume percentage (0-100) and a container width to a pixel width.
/// Used for the custom volume meter bar.
/// </summary>
public class VolumeToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2
            && values[0] is int volume
            && values[1] is double containerWidth
            && containerWidth > 0)
        {
            return Math.Max(0, Math.Min(containerWidth, containerWidth * volume / 100.0));
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
