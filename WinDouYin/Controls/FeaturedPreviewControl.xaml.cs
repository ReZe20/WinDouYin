
using WinDouYin.Contracts;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace WinDouYin.Controls;

/// <summary>
/// 精选卡的静音预览（大卡自动播 + 小卡悬停播共用）。
///
/// 取数不绕过应用那条链：<see cref="HttpRangeStream"/> 自备 UA/Referer、先 <c>PrimeAsync</c> 对质 CDN，
/// 再 <see cref="MediaSource.CreateFromStream"/> 交给 MF——和舞台同一个口径。失败就安静停住（网格里不弹错）。
///
/// 收尸必须延后：MF 还在读最后几块时 Dispose 会把 RO_E_CLOSED 裹成 0xC000027B（舞台那边踩过，
/// 见 MfStageControl 的注释）。这里同样"摘源 → 3 秒后线程池 Dispose"。
/// </summary>
public sealed partial class FeaturedPreviewControl : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(string),
        typeof(FeaturedPreviewControl),
        new PropertyMetadata(string.Empty, OnSourceChanged));

    private readonly MediaPlayer _player = new()
    {
        IsMuted = true,
        IsLoopingEnabled = true,
        AutoPlay = true,
    };

    private int _version;
    private bool _loaded;
    private HttpRangeStream? _stream;
    private MediaSource? _media;

    public FeaturedPreviewControl()
    {
        InitializeComponent();
        Video.SetMediaPlayer(_player);
        _player.PlaybackSession.PositionChanged += OnPositionChanged;
        Loaded += (_, _) =>
        {
            _loaded = true;
            if (Source is { Length: > 0 } source)
            {
                Play(source);
            }
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            Stop();
        };
        Bar.Visibility = Visibility.Collapsed;
        TimeText.Visibility = Visibility.Collapsed;
    }

    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FeaturedPreviewControl control)
        {
            return;
        }

        string url = e.NewValue as string ?? string.Empty;
        if (url.Length == 0)
        {
            control.Stop();
            return;
        }

        if (control._loaded)
        {
            control.Play(url);
        }
    }

    private async void Play(string url)
    {
        int version = ++_version;
        StopCurrent();
        TimeText.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        TimeText.Text = string.Empty;

        HttpRangeStream? stream = null;
        MediaSource? media = null;
        try
        {
            stream = new HttpRangeStream(url);
            await stream.PrimeAsync();
            if (version != _version)
            {
                stream.Dispose();
                return;
            }

            media = MediaSource.CreateFromStream(stream.AsRandomAccessStream(), "video/mp4");
            if (version != _version)
            {
                media.Dispose();
                stream.Dispose();
                return;
            }

            _stream = stream;
            _media = media;
            _player.Source = media;
            _player.Play();
        }
        catch (Exception ex)
        {
            // 预览失败不打扰网格：把源撤了，封面继续显示
            MfLog.Write("app: 精选预览装载失败 " + ex.GetType().Name + " · " + ex.Message);
            try
            {
                media?.Dispose();
                stream?.Dispose();
            }
            catch
            {
                // 忽略
            }

            if (version == _version)
            {
                Bar.Visibility = Visibility.Collapsed;
                TimeText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Stop()
    {
        _version++;
        StopCurrent();
        Bar.Visibility = Visibility.Collapsed;
        TimeText.Visibility = Visibility.Collapsed;
    }

    private void StopCurrent()
    {
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 精选预览停源抛了 " + ex.GetType().Name + " · " + ex.Message);
        }

        MediaSource? media = _media;
        HttpRangeStream? stream = _stream;
        _media = null;
        _stream = null;
        if (media is null && stream is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            try
            {
                media?.Dispose();
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 精选预览旧源释放抛了 " + ex.GetType().Name + " · " + ex.Message);
            }

            try
            {
                stream?.Dispose();
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 精选预览旧流释放抛了 " + ex.GetType().Name + " · " + ex.Message);
            }
        });
    }

    private void OnPositionChanged(MediaPlaybackSession sender, object args)
    {
        double total = sender.NaturalDuration.TotalSeconds;
        if (!(total > 0))
        {
            return;
        }

        double position = sender.Position.TotalSeconds;
        double ratio = Math.Clamp(position / total, 0, 1);
        string text = Clock(position) + " / " + Clock(total);

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (Bar.Visibility != Visibility.Visible)
            {
                return;
            }

            Bar.Value = ratio;
            TimeText.Text = text;
        });
    }

    private static string Clock(double seconds)
    {
        int whole = (int)Math.Max(0, seconds);
        int hours = whole / 3600;
        int minutes = whole % 3600 / 60;
        int secs = whole % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{secs:00}"
            : $"{minutes:00}:{secs:00}";
    }
}
