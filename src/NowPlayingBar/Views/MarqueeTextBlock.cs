using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NowPlayingBar.Views;

public class MarqueeTextBlock : Control
{
    private Canvas? _canvas;
    private TextBlock? _textBlock;
    private TranslateTransform? _transform;
    private Storyboard? _storyboard;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextOrSpeedChanged));

    public static readonly DependencyProperty SpeedProperty =
        DependencyProperty.Register(nameof(Speed), typeof(double), typeof(MarqueeTextBlock),
            new FrameworkPropertyMetadata(30.0, OnTextOrSpeedChanged));

    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(MarqueeTextBlock),
            new PropertyMetadata(Brushes.White));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    static MarqueeTextBlock()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MarqueeTextBlock), new FrameworkPropertyMetadata(typeof(MarqueeTextBlock)));
    }

    public MarqueeTextBlock()
    {
        ClipToBounds = true;
        Loaded += (_, _) => UpdateMarquee();
        SizeChanged += (_, _) => UpdateMarquee();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _storyboard?.Resume();
            }
            else
            {
                _storyboard?.Pause();
            }
        };
    }

    private static void OnTextOrSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarqueeTextBlock marquee)
        {
            marquee.UpdateMarquee();
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _canvas = GetTemplateChild("PART_Canvas") as Canvas;
        _textBlock = GetTemplateChild("PART_TextBlock") as TextBlock;

        if (_textBlock != null)
        {
            _transform = new TranslateTransform();
            _textBlock.RenderTransform = _transform;
        }

        UpdateMarquee();
    }

    private void UpdateMarquee()
    {
        if (_textBlock == null || _canvas == null || _transform == null)
            return;

        _storyboard?.Stop();
        _storyboard = null;
        _transform.X = 0;

        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0)
        {
            OpacityMask = null;
            return;
        }

        // Measure formatted text width
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var formattedText = new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double textWidth = formattedText.Width;
        double availableWidth = ActualWidth;

        if (textWidth > availableWidth)
        {
            // Apply fade gradient mask on edges
            var mask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0.0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.05));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.95));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
            OpacityMask = mask;

            double scrollDistance = textWidth - availableWidth + 24; // extra buffer
            double durationSeconds = Math.Max(2.0, scrollDistance / Math.Max(10.0, Speed));

            var animation = new DoubleAnimationUsingKeyFrames();
            Timeline.SetDesiredFrameRate(animation, 45); // Limit FPS to save GPU/CPU while maintaining smoothness

            // 1. Initial pause
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));

            // 2. Scroll to end
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5 + durationSeconds))));

            // 3. Pause at end
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.0 + durationSeconds))));

            // 4. Return to start
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.0 + durationSeconds))));

            var sb = new Storyboard
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(sb, 45);
            Storyboard.SetTarget(animation, _textBlock);
            Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));

            sb.Children.Add(animation);
            _storyboard = sb;
            sb.Begin();
        }
        else
        {
            // Fits statically: no animation, no CPU load
            OpacityMask = null;
        }
    }
}
