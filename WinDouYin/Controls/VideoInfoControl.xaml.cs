
using WinDouYin.Contracts;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace WinDouYin.Controls;

/// <summary>
/// 信息区浮层（画面左下）：作者行 @昵称 · 相对时间，下面是描述，#话题在本地解析成黄字加粗。
/// 只吃展示数据：作者昵称、预格式化好的时间文本、描述原文。
/// </summary>
public sealed partial class VideoInfoControl : UserControl
{
    // 官方取样：正文纯白；时间与"·"约九成白（叠在模糊底上实测 241/255）；话题 (250,206,21)
    private static readonly SolidColorBrush NameBrush = new(Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush DimBrush = new(Color.FromArgb(230, 255, 255, 255));
    private static readonly SolidColorBrush TagBrush = new(Color.FromArgb(255, 250, 206, 21));

    // 行宽/字号跟 XAML 里的 MaxWidth=300、作者行两档字号保持一致，量宽用的探针也必须同字体
    private const double InfoMaxWidth = 300;
    private const double NameFontSize = 18;
    private const double TimeFontSize = 12;
    private const string Separator = "  ·  ";

    // “图文”胶囊：字号与 XAML 一致；宽度 = 左间距 8 + 左右内边距 12 + 左右边框 2
    private const double BadgeFontSize = 11;
    private const double BadgeChromeWidth = 22;
    private const string BadgeText = "图文";

    // 图文切换两颗 22px 按钮 + 2px 间距 + 6px 左距（见 XAML）
    private const double ImageNavWidth = 52;

    private static readonly FontFamily YaHei = new("Microsoft YaHei");
    private static TextBlock? _probe;

    public static readonly DependencyProperty AuthorProperty = DependencyProperty.Register(
        nameof(Author),
        typeof(string),
        typeof(VideoInfoControl),
        new PropertyMetadata(string.Empty, OnDataChanged));

    public static readonly DependencyProperty TimeTextProperty = DependencyProperty.Register(
        nameof(TimeText),
        typeof(string),
        typeof(VideoInfoControl),
        new PropertyMetadata(string.Empty, OnDataChanged));

    public static readonly DependencyProperty DescProperty = DependencyProperty.Register(
        nameof(Desc),
        typeof(string),
        typeof(VideoInfoControl),
        new PropertyMetadata(string.Empty, OnDataChanged));

    public static readonly DependencyProperty FriendRecommenderProperty = DependencyProperty.Register(
        nameof(FriendRecommender),
        typeof(string),
        typeof(VideoInfoControl),
        new PropertyMetadata(string.Empty, OnDataChanged));

    public static readonly DependencyProperty IsImagePostProperty = DependencyProperty.Register(
        nameof(IsImagePost),
        typeof(bool),
        typeof(VideoInfoControl),
        new PropertyMetadata(false, OnDataChanged));

    public static readonly DependencyProperty ImageCountProperty = DependencyProperty.Register(
        nameof(ImageCount),
        typeof(int),
        typeof(VideoInfoControl),
        new PropertyMetadata(0, OnDataChanged));

    public static readonly DependencyProperty ImagePageProperty = DependencyProperty.Register(
        nameof(ImagePage),
        typeof(int),
        typeof(VideoInfoControl),
        new PropertyMetadata(0, OnDataChanged));

    public VideoInfoControl()
    {
        InitializeComponent();
        AuthorLine.Foreground = NameBrush;
        TimeLine.Foreground = DimBrush;
        FriendLine.Foreground = DimBrush;
    }

    /// <summary>作者昵称，不含 @。</summary>
    public string? Author
    {
        get => (string?)GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }

    /// <summary>相对时间文本（"刚刚"/"3天前"…），空串则不显示"· 时间"段。</summary>
    public string? TimeText
    {
        get => (string?)GetValue(TimeTextProperty);
        set => SetValue(TimeTextProperty, value);
    }

    /// <summary>描述原文，#话题在本地解析。</summary>
    public string? Desc
    {
        get => (string?)GetValue(DescProperty);
        set => SetValue(DescProperty, value);
    }

    /// <summary>朋友推荐人（名字已排好；空串则不显示这一行）。</summary>
    public string? FriendRecommender
    {
        get => (string?)GetValue(FriendRecommenderProperty);
        set => SetValue(FriendRecommenderProperty, value);
    }

    /// <summary>这条是图文/动图帖：作者名右侧显示“图文”胶囊。</summary>
    public bool IsImagePost
    {
        get => (bool)GetValue(IsImagePostProperty);
        set => SetValue(IsImagePostProperty, value);
    }

    /// <summary>本图文帖的图片数（多图才在胶囊右边露切换按钮）。</summary>
    public int ImageCount
    {
        get => (int)GetValue(ImageCountProperty);
        set => SetValue(ImageCountProperty, value);
    }

    /// <summary>当前第几张（0 基）。</summary>
    public int ImagePage
    {
        get => (int)GetValue(ImagePageProperty);
        set => SetValue(ImagePageProperty, value);
    }

    /// <summary>点了上一张/下一张：方向 -1 / +1，交给舞台翻页。</summary>
    public event EventHandler<int>? ImageStepRequested;

    private void OnImagePrevClick(object sender, RoutedEventArgs e) => ImageStepRequested?.Invoke(this, -1);

    private void OnImageNextClick(object sender, RoutedEventArgs e) => ImageStepRequested?.Invoke(this, 1);

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((VideoInfoControl)d).Refresh();

    private void Refresh()
    {
        string author = Author ?? string.Empty;
        string time = TimeText ?? string.Empty;

        bool multipleImages = IsImagePost && ImageCount > 1;
        ImagePostBadge.Visibility = IsImagePost ? Visibility.Visible : Visibility.Collapsed;
        ImageNav.Visibility = multipleImages ? Visibility.Visible : Visibility.Collapsed;
        ImagePrevButton.IsEnabled = multipleImages && ImagePage > 0;
        ImageNextButton.IsEnabled = multipleImages && ImagePage < ImageCount - 1;

        if (author.Length > 0)
        {
            // 先量出"· 时间"、"图文"胶囊和切换按钮要多宽，从 300 里扣掉后剩下的才是名字的地盘——
            // 放不下只截名字，右边几段永远完整（官方同名长时的行为）
            double tailWidth = time.Length > 0 ? MeasureWidth(Separator + time, TimeFontSize, semibold: false) : 0;
            if (IsImagePost)
            {
                tailWidth += BadgeChromeWidth + MeasureWidth(BadgeText, BadgeFontSize, semibold: false);
            }

            if (multipleImages)
            {
                tailWidth += ImageNavWidth;
            }

            AuthorLine.Text = FitName(author, tailWidth);
        }

        AuthorLine.Visibility = author.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        TimeLine.Text = time.Length > 0 ? Separator + time : string.Empty;
        TimeLine.Visibility = time.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        string friend = FriendRecommender ?? string.Empty;
        FriendLine.Text = friend.Length > 0 ? "朋友 " + friend + " 推荐" : string.Empty;
        FriendLine.Visibility = friend.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        string desc = Desc ?? string.Empty;
        DescBlock.Inlines.Clear();
        if (desc.Length > 0)
        {
            AppendDesc(desc);
        }

        DescBlock.Visibility = desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 名字（含前导 @）放得下就不动；放不下从尾部逐字缩，缩到"前缀…"+右侧整段一起 ≤ 300 为止。
    /// 名字栏没有尾部 trim 可用（TextTrimming 会把时间一起咬掉），所以宽度在代码里自己量、自己截。
    /// </summary>
    private static string FitName(string name, double tailWidth)
    {
        string full = "@" + name;
        double budget = InfoMaxWidth - tailWidth;
        if (MeasureWidth(full, NameFontSize, semibold: true) <= budget)
        {
            return full;
        }

        for (int take = name.Length - 1; take > 0; take--)
        {
            string candidate = "@" + name[..take] + "…";
            if (MeasureWidth(candidate, NameFontSize, semibold: true) <= budget)
            {
                return candidate;
            }
        }

        return "@…";
    }

    /// <summary>用离屏探针 TextBlock 量单行宽度，跟屏幕上的排版同一个文本栈，误差在 1px 内。</summary>
    private static double MeasureWidth(string text, double fontSize, bool semibold)
    {
        _probe ??= new TextBlock { FontFamily = YaHei };
        _probe.FontSize = fontSize;
        _probe.FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal;
        _probe.Text = text;
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _probe.DesiredSize.Width;
    }

    /// <summary># 起到下一个 # 或空白为止算一个话题，行内跟正文一起折行。</summary>
    private void AppendDesc(string desc)
    {
        int at = 0;
        while (at < desc.Length)
        {
            int hash = desc.IndexOf('#', at);
            if (hash < 0)
            {
                AddRun(desc[at..], tag: false);
                return;
            }

            if (hash > at)
            {
                AddRun(desc[at..hash], tag: false);
            }

            int end = hash + 1;
            while (end < desc.Length && desc[end] != '#' && !char.IsWhiteSpace(desc[end]))
            {
                end++;
            }

            // 光一个 "#"（后面就是空白或结尾）不算话题，按正文走
            AddRun(desc[hash..end], tag: end > hash + 1);
            at = end;
        }
    }

    private void AddRun(string text, bool tag) =>
        DescBlock.Inlines.Add(new Run
        {
            Text = text,
            Foreground = tag ? TagBrush : NameBrush,
            FontWeight = tag ? FontWeights.SemiBold : FontWeights.Normal,
        });
}
