using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using WhisperDesk.ViewModels;
using WhisperDesk.Views;
using WhisperDesk.Themes;
using Xunit;

namespace WhisperDesk.Tests;

public class LightThemeTests
{
    [Fact]
    public void Theme_LoadsStylesAndSelectedPalette()
    {
        RunOnSta(() =>
        {
            var theme = LoadTheme();
            Assert.Equal(Color.FromRgb(0xFC, 0xFD, 0xFE), theme["WhisperDesk.Color.Surface"]);
            Assert.Equal(Color.FromRgb(0x40, 0x5B, 0x70), theme["WhisperDesk.Color.Accent"]);
            Assert.Contains("Microsoft YaHei UI", Assert.IsType<FontFamily>(theme["WhisperDesk.Font"]).Source);

            var button = new Button { Style = Assert.IsType<Style>(theme["WhisperDesk.PrimaryButton"]) };
            Assert.True(button.ApplyTemplate());
            Assert.Equal(40, button.MinHeight);

            var transcript = new TextBox { Style = Assert.IsType<Style>(theme["WhisperDesk.TranscriptTextBox"]) };
            Assert.True(transcript.ApplyTemplate());
            Assert.True(transcript.IsReadOnly);
            Assert.Equal(TextWrapping.Wrap, transcript.TextWrapping);
            Assert.Equal(ScrollBarVisibility.Auto, transcript.VerticalScrollBarVisibility);
        });
    }

    [Fact]
    public void TranscriptExpander_HasAnAlignedTransparentHeaderAndStillToggles()
    {
        RunOnSta(() =>
        {
            var theme = LoadTheme();
            var expander = new Expander
            {
                Style = Assert.IsType<Style>(theme["WhisperDesk.TranscriptExpander"]),
                Header = "查看原文",
                Content = new TextBox
                {
                    Style = Assert.IsType<Style>(theme["WhisperDesk.TranscriptTextBox"]),
                    Text = "保留可选择、可复制的原始转写。",
                    MaxHeight = 120
                }
            };
            expander.Resources.MergedDictionaries.Add(new CustomColorTheme
            {
                BaseTheme = BaseTheme.Light,
                PrimaryColor = Color.FromRgb(0x40, 0x5B, 0x70),
                SecondaryColor = Color.FromRgb(0x40, 0x5B, 0x70)
            });
            expander.Resources.MergedDictionaries.Add(Assert.IsType<ResourceDictionary>(
                Application.LoadComponent(new Uri(
                    "/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml", UriKind.Relative))));
            expander.Resources.MergedDictionaries.Add(theme);
            Assert.True(expander.ApplyTemplate());
            var header = Assert.IsType<ToggleButton>(expander.Template.FindName("HeaderButton", expander));
            header.ApplyTemplate();
            var surface = Assert.IsType<Border>(header.Template.FindName("HeaderSurface", header));
            var label = Assert.IsType<ContentPresenter>(header.Template.FindName("HeaderLabel", header));
            var chevron = Assert.IsType<System.Windows.Shapes.Path>(header.Template.FindName("Chevron", header));
            var content = Assert.IsType<ContentPresenter>(expander.Template.FindName("ExpandedContent", expander));

            expander.Measure(new Size(360, double.PositiveInfinity));
            expander.Arrange(new Rect(0, 0, 360, expander.DesiredSize.Height));
            expander.UpdateLayout();
            Assert.Equal(360, header.ActualWidth);
            Assert.True(label.ActualWidth >= 48, $"The four-character header is clipped to {label.ActualWidth}px.");
            Assert.True(chevron.TranslatePoint(new Point(), expander).X >= 340, "The chevron should stay at the right edge.");
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(surface.Background).Color.A);
            Assert.Equal(0, label.TranslatePoint(new Point(), expander).X);
            Assert.Equal(36, expander.ActualHeight);
            Assert.Equal(Visibility.Collapsed, content.Visibility);

            header.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Assert.True(expander.IsExpanded);
            Assert.Equal(Visibility.Visible, content.Visibility);

            expander.IsExpanded = false;
            Assert.False(header.IsChecked);
            Assert.Equal(Visibility.Collapsed, content.Visibility);
        });
    }

    [Fact]
    public void DialogTransitions_StartImmediatelyAndUseUniformDuration()
    {
        RunOnSta(() =>
        {
            var host = new DialogHost();
            host.Resources.MergedDictionaries.Add(new CustomColorTheme
            {
                BaseTheme = BaseTheme.Light,
                PrimaryColor = Color.FromRgb(0x40, 0x5B, 0x70),
                SecondaryColor = Color.FromRgb(0x40, 0x5B, 0x70)
            });
            host.Resources.MergedDictionaries.Add(Assert.IsType<ResourceDictionary>(
                Application.LoadComponent(new Uri(
                    "/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml", UriKind.Relative))));
            host.ApplyTemplate();
            var root = Assert.IsAssignableFrom<FrameworkElement>(host.Template.FindName("DialogHostRoot", host));
            var states = VisualStateManager.GetVisualStateGroups(root).OfType<VisualStateGroup>()
                .Single(group => group.Name == "PopupStates");
            var opening = states.Transitions.OfType<VisualTransition>()
                .Single(item => item.From == "Closed" && item.To == "Open").Storyboard;
            Assert.NotNull(opening);

            DialogTransitions.UseUniformTiming(host);
            DialogTransitions.UseUniformTiming(host);

            var updatedOpening = states.Transitions.OfType<VisualTransition>()
                .Single(item => item.From == "Closed" && item.To == "Open").Storyboard;
            Assert.NotNull(updatedOpening);
            Assert.NotSame(opening, updatedOpening);
            var closing = states.Transitions.OfType<VisualTransition>()
                .Single(item => item.From == "Open" && item.To == "Closed").Storyboard;
            Assert.NotNull(closing);
            Assert.Equal(TimeSpan.FromMilliseconds(240), closing.Duration.TimeSpan);
            foreach (var animation in closing.Children.OfType<DoubleAnimationUsingKeyFrames>())
            {
                Assert.Equal(2, animation.KeyFrames.Count);
                Assert.Equal(TimeSpan.Zero, animation.KeyFrames[0].KeyTime.TimeSpan);
                Assert.Equal(TimeSpan.FromMilliseconds(240), animation.KeyFrames[1].KeyTime.TimeSpan);
                Assert.NotEqual(animation.KeyFrames[0].Value, animation.KeyFrames[1].Value);
            }
            var visibility = Assert.Single(closing.Children.OfType<BooleanAnimationUsingKeyFrames>());
            Assert.Equal(TimeSpan.FromMilliseconds(240),
                Assert.Single(visibility.KeyFrames.OfType<BooleanKeyFrame>()).KeyTime.TimeSpan);
            Assert.All(updatedOpening.Children.OfType<DoubleAnimationUsingKeyFrames>(),
                animation => Assert.Equal(TimeSpan.FromMilliseconds(240), animation.KeyFrames[^1].KeyTime.TimeSpan));
            var show = Assert.Single(updatedOpening.Children.OfType<BooleanAnimationUsingKeyFrames>());
            Assert.Equal(TimeSpan.Zero, Assert.Single(show.KeyFrames.OfType<BooleanKeyFrame>()).KeyTime.TimeSpan);
        });
    }

    [Fact]
    public void Theme_NormalTextHasAccessibleContrast()
    {
        RunOnSta(() =>
        {
            var theme = LoadTheme();
            var pairs = new[]
            {
                ("Text", "Surface"),
                ("SecondaryText", "Surface"),
                ("SecondaryText", "Subtle"),
                ("Accent", "Surface"),
                ("Success", "Surface"),
                ("Danger", "DangerSubtle")
            };
            foreach (var (foreground, background) in pairs)
            {
                var ratio = Contrast(
                    Assert.IsType<Color>(theme[$"WhisperDesk.Color.{foreground}"]),
                    Assert.IsType<Color>(theme[$"WhisperDesk.Color.{background}"]));
                Assert.True(ratio >= 4.5, $"{foreground} on {background}: {ratio:F2}");
            }
            Assert.True(Contrast(Colors.White, Assert.IsType<Color>(theme["WhisperDesk.Color.Accent"])) >= 4.5);
            Assert.True(Contrast(Colors.White, Assert.IsType<Color>(theme["WhisperDesk.Color.Danger"])) >= 4.5);
        });
    }

    [Fact]
    public void SettingsDialog_KeepsItsSizeAndButtonsStableAcrossLoadingStates()
    {
        RunOnSta(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.Resources.MergedDictionaries.Add(new CustomColorTheme
                {
                    BaseTheme = BaseTheme.Light,
                    PrimaryColor = Color.FromRgb(0x40, 0x5B, 0x70),
                    SecondaryColor = Color.FromRgb(0x40, 0x5B, 0x70)
                });
                app.Resources.MergedDictionaries.Add(Assert.IsType<ResourceDictionary>(
                    Application.LoadComponent(new Uri(
                        "/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml", UriKind.Relative))));
                app.Resources.MergedDictionaries.Add(LoadTheme());

                var dialog = new SettingsDialog();
                var apply = Assert.IsType<Button>(dialog.FindName("ApplyButton"));
                var cancel = Assert.IsType<Button>(dialog.FindName("CancelButton"));
                var viewport = Assert.IsType<Grid>(dialog.FindName("DeviceViewport"));
                var devices = Assert.IsType<ScrollViewer>(dialog.FindName("DeviceScrollViewer"));
                var errors = Assert.IsType<ScrollViewer>(dialog.FindName("ErrorScrollViewer"));
                Size? initialSize = null;
                Point? initialApplyPosition = null;
                Point? initialCancelPosition = null;

                var states = new[]
                {
                    (DeviceCount: 0, Loading: true, Error: ""),
                    (DeviceCount: 2, Loading: true, Error: ""),
                    (DeviceCount: 1, Loading: false, Error: ""),
                    (DeviceCount: 2, Loading: false, Error: ""),
                    (DeviceCount: 12, Loading: false, Error: ""),
                    (DeviceCount: 0, Loading: false, Error: ""),
                    (DeviceCount: 0, Loading: false, Error: string.Concat(Enumerable.Repeat("Device connection failed. ", 100)))
                };

                foreach (var state in states)
                {
                    dialog.DataContext = new
                    {
                        IsLoading = state.Loading,
                        HasError = state.Error.Length > 0,
                        ErrorMessage = state.Error,
                        NoDevicesFound = !state.Loading && state.DeviceCount == 0 && state.Error.Length == 0,
                        Devices = Enumerable.Range(0, state.DeviceCount).Select(i => new MicrophoneItem
                        {
                            Id = $"device-{i}",
                            DisplayName = $"Microphone {i} with a long device name",
                            IsSelected = i == 0,
                            Volume = 50
                        }).ToArray(),
                        ApplyCommand = new RelayCommand(() => { },
                            () => !state.Loading && state.DeviceCount > 0 && state.Error.Length == 0)
                    };
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                    dialog.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    dialog.Arrange(new Rect(dialog.DesiredSize));
                    dialog.UpdateLayout();

                    initialSize ??= dialog.DesiredSize;
                    initialApplyPosition ??= apply.TranslatePoint(new Point(), dialog);
                    initialCancelPosition ??= cancel.TranslatePoint(new Point(), dialog);
                    Assert.Equal(initialSize.Value, dialog.DesiredSize);
                    Assert.Equal(initialApplyPosition.Value, apply.TranslatePoint(new Point(), dialog));
                    Assert.Equal(initialCancelPosition.Value, cancel.TranslatePoint(new Point(), dialog));
                    Assert.True(viewport.ActualHeight >= 100, $"Content viewport is only {viewport.ActualHeight}px high.");
                    Assert.True(initialApplyPosition.Value.Y + apply.ActualHeight <= dialog.ActualHeight);

                    if (!state.Loading && state.DeviceCount == 12)
                        Assert.True(devices.ScrollableHeight > 0, "Long device lists must scroll instead of resizing the dialog.");
                    if (state.Error.Length > 0)
                        Assert.True(errors.ScrollableHeight > 0, "Long errors must scroll instead of moving the buttons.");
                }
            }
            finally
            {
                app.Shutdown();
            }
        });
    }

    private static ResourceDictionary LoadTheme() =>
        Assert.IsType<ResourceDictionary>(Application.LoadComponent(
            new Uri("/WhisperDesk;component/Themes/LightTheme.xaml", UriKind.Relative)));

    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF theme test timed out.");
        failure?.Throw();
    }
}
