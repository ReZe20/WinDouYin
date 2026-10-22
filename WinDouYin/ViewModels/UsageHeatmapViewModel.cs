
using WinDouYin.Contracts;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using WinDouYin.Service;

namespace WinDouYin.ViewModels;

/// <summary>热力图的一格：哪一天、累计看了多久、强调色那层铺多浓。</summary>
public sealed class UsageHeatmapDay
{
    public UsageHeatmapDay(DateOnly date, double seconds, double fillOpacity)
    {
        Date = date;
        Seconds = seconds;
        FillOpacity = fillOpacity;
        // 条带右端就是"这一天以里最近的那个真日子"（选今年=今天，选往年=那年 12/31），
        // 未来的一天都不画，所以这里也不必有"还没到"那一句（2026-10-22 口述"未来的不造格子"）。
        Tooltip = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · "
            + (seconds > 0 ? "看了 " + UsageHeatmapViewModel.DurationText(seconds) : "没有观看");
    }

    public DateOnly Date { get; }

    public double Seconds { get; }

    /// <summary>0（那天没看，只留中性底那一层）/ 0.25 / 0.5 / 0.75 / 1。
    /// 底色与填充色都由 XAML 那侧的 ThemeResource 给（<c>ControlFillColorSecondary</c> 与
    /// <c>AccentFillColorDefault</c>＝Windows 强调色），这里只报浓淡：换主题、换用户的强调色都不用重算这张图。</summary>
    public double FillOpacity { get; }

    public string Tooltip { get; }
}

/// <summary>
/// 月份标签的一枚：<b>每列一枚</b>，不该亮的列给空串。
/// 为什么每列都占一格而不是只装亮着的那几枚：格子行与标签行各是一台 <c>ItemsRepeater</c> 配
/// <c>UniformGridLayout</c>，两台的列数与间距一致才能对齐——标签按列铺满，第 k 枚天生落在第 k 列底下。
/// 早先那套"只装亮着的 + 前面垫一段算出来的空白"会漂：额度按 60 DIP 记、字实际只有 30 上下，
/// 一枚偏一点累积下来月份就落错列（2026-10-21 实测）。
/// </summary>
public sealed record UsageHeatmapMonthLabel(string Text);

/// <summary>
/// 年份标签的一枚：也是<b>每列一枚</b>，摆在这条带的<b>上面</b>，整行<b>常亮</b>
/// （2026-10-22 口述"即使视图内只有一年，也要显示年份"）。
/// 按"最左那一列报它第一天所在的年，之后哪一列撞上 1 月 1 号就在这列报那一年"给，
/// 其余列空串——与月份那台同样按列铺满，两台的列才对得齐（理由见 <see cref="UsageHeatmapMonthLabel"/>）。
/// </summary>
public sealed record UsageHeatmapYearLabel(string Text);

/// <summary>
/// 本人主页的“使用热力图”：<b>按年份</b>画那一年每天累计看了多久（按实际播放计时）。
/// 一年一档是右边那颗年份选项换来的口径（2026-10-21 口述“过长可以链接下一年，即使那一年没有数据”），选项照旧留。
/// <b>格子是固定大小</b>：16 DIP 一方、列间 4 DIP，<c>ItemsStretch=None</c>（2026-10-22 口述
/// "现在是自动缩放填充面板，改成固定大小，用之前的大小"——"之前的大小"就是最初那版
/// 16×16 + 4 间距，<c>aafb279</c> 里 Viewbox 内那颗 Border 的宽高），不再随面板宽缩放。
/// <b>不经 <c>Viewbox</c></b>：那颗 0xC0000005 实测过"Viewbox 与这台 repeater 同树就炸，
/// 与旁边有没有第二台并排 repeater 无关"（仓库笔记第 50 条），**没有新证据别再第三次试**。
/// <b>整条带右对齐</b>（三台 repeater 都 <c>HorizontalAlignment=Right</c>），
/// <b>右端是"这一天以里最近的那个真日子"</b>：选今年就是今天，选往年就是那年的 12/31，
/// <b>未来的一天一律不造格子</b>（同一句口述"最右边是最新的，也就是当天的，未来的不造格子过去的可造"）——
/// 所以最右那一列不满七天也照旧只画到今天，不再像上一版那样把整周补齐、右边挂几天"还没到"。
/// <b>画几列由面板实测宽度给</b>（见 <see cref="SetAvailableWidth"/>）：一列 20 DIP，放得下几列就铺几列；
/// 往右边扩宽窗口时左边不再空缺，多出来的列继续用<b>更早的真日子</b>往左填（可能跨年），
/// 而且这一段<b>不设左边界</b>（2026-10-22 口述"不要设置左边界"——原来的 160 列封顶会在
/// 超宽窗口里把条带贴着右边、左边空出一大截）：窗口多宽就往左铺多远。
/// 条带<b>上面</b>那一行年份<b>常亮</b>（同日口述"即使视图内只有一年，也要显示年份"），
/// 按"最左列报起头年、哪一列撞上 1 月 1 号报那一年"给（<see cref="YearLabels"/>）；
/// 月份行照旧每月 1 号报"N月"，<b>1 月不再让位给年份行</b>（同日口述"少了 1 月的显示"）。
/// 反过来，面板窄到放不下整年时不往右扩，只画得下几周就画几周，说明那句把这件事讲清楚。
/// 年份选项<b>到今年为止</b>，不列未来（同日口述"选择年份不能选择未来"）。
/// 格子那台是<b>列优先</b>（七天一列、<c>Orientation=Vertical</c> + <c>MaximumRowsOrColumns=7</c>；
/// 第九十九轮 F5 实测 Horizontal 是"一行放满"、会折成高塔，包里文档那句与实现相反），
/// 标签两台是 <c>StackLayout Orientation=Horizontal</c> 一枚接一枚往右排：
/// 三台共用"格 16 + 列间 4"这一个档距，加上列数同源，才对得一格对一格。
/// 汇总那句只认<b>选定那一年</b>，与画出去的列数、左边补了多少都没关系。
/// 数据只来自本机 <see cref="UsageStore"/>，不拿账号侧历史（history/read 一页 20 条，一年回填不现实）。
/// 浓淡分五档：0 / 1-9 分钟 / 10-29 分钟 / 30-59 分钟 / 60 分钟以上，走 Windows 强调色
/// （<c>AccentFillColorDefault</c> 那一层，浓淡由每格的 FillOpacity 报）；
/// 图例只写“少 → 多”，具体时长在格子的 tooltip 里。
/// </summary>
public sealed partial class UsageHeatmapViewModel : INotifyPropertyChanged
{
    /// <summary>一格的<b>固定</b>尺寸与行列间距：XAML 那侧 <c>UniformGridLayout</c> 的
    /// <c>MinItemWidth/MinItemHeight/MinRowSpacing/MinColumnSpacing</c> 与格子模板的 <c>Width/Height</c>
    /// 都写死这四个数，<c>ItemsStretch=None</c> 不再缩放（2026-10-22 口述"改成固定大小，用之前的大小"）。
    /// 列数改由面板宽度换算：<see cref="ColumnPitch"/> 就是"一列占多少 DIP"。</summary>
    public const double CellSize = 16;
    public const double ColumnSpacing = 4;
    public const double ColumnPitch = CellSize + ColumnSpacing;

    /// <summary>一列几天 = 几行（一列就是连续的七天，右端那一列可以不满），
    /// 格子那台 <c>UniformGridLayout.MaximumRowsOrColumns</c> 就是这个 7。</summary>
    public const int DaysPerColumn = 7;

    /// <summary>年份选项最远往前列几年：防一本被写花的账（掺进 1900 年）把选项列满屏。</summary>
    private const int MaxYearBack = 5;

    private int _year = DateOnly.FromDateTime(DateTime.Now).Year;
    private int _columnCount;
    private double _stripWidth;

    /// <summary>面板实测宽度换算出来的列数；0 = 还没量到（这时一格都不建，等页里 <c>SizeChanged</c> 报宽再铺）。</summary>
    private int _wantedColumns;

    private string _summary = string.Empty;
    private string _note = string.Empty;
    private Visibility _noteVisibility = Visibility.Visible;

    /// <summary>账本内容 + 年份 + 列数 + 当天日期的指纹：没变就一个格子都不重建
    /// （进页会连调两次、拖窗沿会连发几十次，多出来的那些发都空转在这一条上）。
    /// 年份与列数都必须进指纹——不然换到一本同样总数的空年份、或窗口拉宽又缩回去（列数没变），
    /// 都会被判成"图没变"。</summary>
    private string _stamp = string.Empty;

    /// <summary>同一时刻可能有多发在飞（进页一发、身份对齐后一发、换年一发）：只让最新那发落地。</summary>
    private int _refreshGen;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>这一条带的格子，<b>按列优先平铺</b>（一天接一天，七天一列，最后一列可以不满七天）。
    /// 布局那侧 <c>Orientation=Horizontal</c> + <c>MaximumRowsOrColumns=7</c> 认的就是这个顺序：
    /// 一列放满七天再往右换列。ItemsRepeater.ItemsSource 走 WinRT 向量：必须是 ObservableCollection，
    /// 交 IReadOnlyList 会在 x:Bind 设源时抛 "Argument 'source' is not a supported vector"。下面两台标签同理。</summary>
    public ObservableCollection<UsageHeatmapDay> Days { get; } = [];

    /// <summary>月份标签，<b>每列一枚</b>（不该亮的给空串），摆在格子那台的<b>下面</b>。</summary>
    public ObservableCollection<UsageHeatmapMonthLabel> MonthLabels { get; } = [];

    /// <summary>年份标签，同样<b>每列一枚</b>，摆在格子那台的<b>上面</b>，整行常亮（不跨年也显示）。</summary>
    public ObservableCollection<UsageHeatmapYearLabel> YearLabels { get; } = [];

    /// <summary>右边那颗年份选项的候选：账本最早年（最远退 <see cref="MaxYearBack"/> 年）到今年为止，
    /// <b>不列未来</b>（2026-10-22 口述"选择年份不能选择未来"）。</summary>
    public ObservableCollection<int> Years { get; } = [];

    /// <summary>当前看的是哪一年。</summary>
    public int Year => _year;

    /// <summary>当前年在候选里的第几档。
    /// XAML 那侧绑的是 <c>ComboBox.SelectedIndex</c> 而不是 <c>SelectedItem</c>：
    /// 编译绑定要类型对得上，<c>int</c> 去顶 <c>object</c> 那条装箱转换在 x:Bind 里不可靠，
    /// 而 SelectedIndex 本身就是 int，绑它零风险。候选不在（刚换号、列表重排）就报 0。</summary>
    public int YearIndex
    {
        get
        {
            int index = Years.IndexOf(_year);
            return index < 0 ? 0 : index;
        }
    }

    /// <summary>这一条带画几列（= 几周）：照面板实测宽度放得下几列给，还没量到就是 0（一格不画）。
    /// 界面那侧只有 <see cref="StripWidth"/> 进 XAML（三台 repeater 的宽），列数本身不进——
    /// 格子那台的 <c>MaximumRowsOrColumns</c> 写死 7，标签两台走 <c>StackLayout</c> 按枚往右排；
    /// 两个数同出一处（都从这里的列数算），才对得齐。</summary>
    public int ColumnCount => _columnCount;

    /// <summary>“2026 年看过 12 小时 40 分钟 · 50 天有记录”。</summary>
    public string Summary => _summary;

    /// <summary>那一年没数据、或本机账本还没到那一年时，这句要留着说明为什么是空的。</summary>
    public string Note => _note;

    public Visibility NoteVisibility => _noteVisibility;

    /// <summary>换年份：立刻重画那一年。只认 <see cref="Years"/> 里给出的年份，
    /// 且与当前相同就当没这回事——选项被程序改选时也会走一发，不收口就会自转一圈。</summary>
    public void SelectYear(int year)
    {
        if (year == _year || !Years.Contains(year))
        {
            return;
        }

        _year = year;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Year)));
        _ = RefreshAsync();
    }

    /// <summary>列数与档距算出的条带宽（<c>列数 × 20 − 4</c>，列数为 0 时给 0）。
    /// 三台 repeater 都把这个数写进 <c>Width</c> 再 <c>HorizontalAlignment=Right</c>：
    /// repeater 自己有确定宽，量出来的就是这条带的宽，右对齐才有东西可对齐——
    /// 只写 HorizontalAlignment 不可靠（ItemsRepeater 量到的若是被可用宽夹住的宽，它就铺满整格、
    /// 条带反而贴左，右边留空缺，正是要修的那件事）。列数是照宽度算的，所以这个数永远不大于可用宽，
    /// 不会顶出横向滚动条。</summary>
    public double StripWidth => _stripWidth;

    /// <summary>页里那张卡的 <c>SizeChanged</c> 把可用宽报进来：<b>换成"放得下几列"</b>，列数没变就一根手指都不动
    /// （拖窗沿会连发几十发，收口只收在这一条——格子是固定 16 DIP，宽度只决定画几天，不决定格子多大）。
    /// 这是本图唯一一处"界面量出来的数进 ViewModel"：条带要<b>右对齐并按宽度往左补更早的日子</b>，
    /// 列数就只能由实测宽度给，纯 XAML 那头没处表达"缺的宽度用日子填"。
    /// 口径取 <c>宽度 / 20</c> 向下取整（一列 = 格 16 + 间距 4）：这样算出的列数换算成条带宽是
    /// <c>列数 × 20 − 4</c>，永远比可用宽小，既顶不出横向滚动条，也不会把最后一列挤到卡片描边外面。
    /// <c>width&lt;=0</c>（还没排过版）报 0：<b>一格都不建</b>，等第一次量到宽再铺。</summary>
    public void SetAvailableWidth(double width)
    {
        // 不封顶（2026-10-22 口述"不要设置左边界"）：窗口多宽就往左铺多远，条带永远吃满可用宽。
        int columns = width > 0 ? Math.Max((int)(width / ColumnPitch), 0) : 0;
        if (columns == _wantedColumns)
        {
            return;
        }

        _wantedColumns = columns;
        _ = RefreshAsync();
    }

    /// <summary>
    /// 重算整张图。读账本（第一次要逐条解 AES-GCM + 读 JSON）与那年几百格对象的构建都在线程池，
    /// UI 线程只做最后那一次集合替换——进本人主页、切回本人主页、换号、换年份、窗口宽度换算成另一档列数
    /// 各调一次（同一发里 <c>UsageStore.Snapshot()</c> 也顺带把账本读新，回到页面时最新那天就在图上）。
    /// 颜色那侧不用管主题（ThemeResource 自己跟），所以换主题不必重算。
    /// </summary>
    public async Task RefreshAsync()
    {
        int gen = ++_refreshGen;
        int year = _year;
        int wantedColumns = _wantedColumns;
        HeatmapSnapshot shot = await Task.Run(() => BuildSnapshot(year, wantedColumns));
        if (gen != _refreshGen || string.Equals(shot.Stamp, _stamp, StringComparison.Ordinal))
        {
            return;   // 又发了一发（让新的落地），或账本没长、日子没翻、年没换、列数没变：一格都不用重建
        }

        _stamp = shot.Stamp;
        _summary = shot.Summary;
        _note = shot.Note;
        _noteVisibility = shot.NoteVisibility;
        _columnCount = shot.Columns;
        _stripWidth = shot.Columns == 0 ? 0 : shot.Columns * ColumnPitch - ColumnSpacing;

        if (shot.Year != _year)
        {
            // 请求的那年不在候选里（比如账本换了、年被挤出去）：以算出来的这年为准，选项跟着走
            _year = shot.Year;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Year)));
        }

        Days.Clear();
        foreach (UsageHeatmapDay day in shot.Days)
        {
            Days.Add(day);
        }

        MonthLabels.Clear();
        foreach (UsageHeatmapMonthLabel label in shot.MonthLabels)
        {
            MonthLabels.Add(label);
        }

        YearLabels.Clear();
        foreach (UsageHeatmapYearLabel label in shot.YearLabels)
        {
            YearLabels.Add(label);
        }

        if (!Years.SequenceEqual(shot.Years))
        {
            Years.Clear();
            foreach (int offered in shot.Years)
            {
                Years.Add(offered);
            }
        }

        NotifyAll();
    }

    /// <summary>线程池那一半：只碰纯 CLR 对象（ObservableCollection 不是 DependencyObject，可以在后台建）。
    /// <paramref name="wantedColumns"/> 是页里报进来的可用宽换算成的列数，0 = 还没量到。</summary>
    private static HeatmapSnapshot BuildSnapshot(int requestedYear, int wantedColumns)
    {
        IReadOnlyDictionary<string, double> secondsByDay = UsageStore.Snapshot();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        int thisYear = today.Year;

        DateOnly? earliest = null;
        foreach (string key in secondsByDay.Keys)
        {
            if (DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly one)
                && (earliest is null || one < earliest))
            {
                earliest = one;
            }
        }

        // 候选年：最早那笔所在年（不认账本被写花，最远退 MaxYearBack 年）到今年为止，不列未来。
        int first = earliest is null
            ? thisYear - 1
            : Math.Max(Math.Min(earliest.Value.Year, thisYear), thisYear - MaxYearBack);
        if (first > thisYear)
        {
            first = thisYear;
        }

        List<int> years = [];
        for (int y = first; y <= thisYear; y++)
        {
            years.Add(y);
        }

        int year = years.Contains(requestedYear) ? requestedYear : thisYear;

        DateOnly jan1 = new(year, 1, 1);
        DateOnly dec31 = new(year, 12, 31);

        // 整年按周取齐有几列（52~54）。这一档现在只用来判"这一屏放不放得下整年"（说明那句要讲），
        // 真正画几列照面板实测宽度给：放得下几列就铺几列，左边不再留空缺（2026-10-22 口述）。
        // 还没量到宽（wantedColumns=0）时<b>一格都不建</b>：固定大小的条带要是先按整年铺 1036 DIP，
        // 窄卡里那一发就会溢出到卡片描边外面，等 SizeChanged 纠正已经晚了一帧。
        DateOnly calStart = jan1.AddDays(-(int)jan1.DayOfWeek);
        DateOnly calEnd = dec31.AddDays(-(int)dec31.DayOfWeek + 6);
        int yearColumns = (calEnd.DayNumber - calStart.DayNumber + 1 + DaysPerColumn - 1) / DaysPerColumn;
        int columns = wantedColumns;

        // 右端 = "这一天以里最近的那个真日子"（2026-10-22 口述"最右边是最新的，也就是当天的，
        // 未来的不造格子过去的可造"）：选今年就是今天，选往年就是那年的 12/31。
        // 上一版把右端取到"今天所在那一周"的周六，右边几天"还没到"也占格——现在未来的一天根本不画，
        // 所以最后一列不满七天就是那列短一截，条带也不再要求每列从周日起（每列就是往前数七天）。
        // 缺口一律往<b>左</b>补，补的是更早的真日子（邻年的），有账就画、没账也照样画空，
        // 而且<b>不设左边界</b>——列数照窗口宽给，多宽就往左铺多远。
        DateOnly gridEnd = today < dec31 ? today : dec31;
        DateOnly gridStart = gridEnd.AddDays(-(columns * DaysPerColumn - 1));

        // 汇总只认这一年内、且已经过去的日子：还没到的那几个月不能算进"看过多久"。
        DateOnly countedEnd = dec31 < today ? dec31 : today;
        double total = 0;
        int activeDays = 0;
        if (jan1 <= countedEnd)
        {
            for (DateOnly day = jan1; day <= countedEnd; day = day.AddDays(1))
            {
                double seconds = secondsByDay.GetValueOrDefault(Key(day));
                if (seconds > 0)
                {
                    total += seconds;
                    activeDays++;
                }
            }
        }

        string summary = total > 0
            ? year + " 年看过 " + DurationText(total) + " · " + activeDays + " 天有记录"
            : year + " 年还没有观看记录";

        // 说明这一行是"能拼就拼"：账本从哪天起、这一屏放得下几周，两件事用户都会问。
        // 排他地只留一句会把另一件说没——账本才几星期大时，"从 X 开始记录"年年抢先，
        // "这一屏只放得下 N 周"就永远看不见（固定大小之后这条最该说，2026-10-22）。
        var parts = new List<string>();
        if (UsageStore.RecordRejected)
        {
            parts.Add("本机账本有日期没能通过校验（可能被改动过），那些日期已丢弃，其余照常");
        }

        if (earliest is null)
        {
            parts.Add("从本版本开始累计：播放时按实际时长计时");
        }
        else if (earliest.Value > gridStart)
        {
            // 账本还没满一条带（本机开始记的日子比条带最左那一格还晚）：讲清左边那段空白的来历。
            parts.Add("本机从 " + Key(earliest.Value) + " 开始记录，之前的日期留空");
        }

        if (wantedColumns > 0 && columns < yearColumns)
        {
            // 格子固定大小、不随面板缩放，窄屏就只画得下离右端最近的这几周；
            // 把"想看更早的日子就把窗口拉宽"这句话留给用户，省得他以为那一年没数据。
            parts.Add("格子是固定大小：这一屏只放得下 " + columns + " 周，把窗口拉宽会往左边补更早的日子");
        }

        string note = string.Join("；", parts);
        Visibility noteVisibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 格子按<b>列优先</b>出（一天接一天，七天一列）：布局那侧 Orientation=Vertical +
        // MaximumRowsOrColumns=7 认的就是这个顺序（"一列放满七天再往右换列"；Horizontal 是
        // "一行放满"，2026-10-22 F5 高塔实测，见 UserProfilePage.xaml 那段朝向口径）。
        // 标签也照这个顺序按列找"这一列里有没有某个月的 1 号"、"这一列里有没有 1 月 1 号"，最省事。
        var days = new List<UsageHeatmapDay>();
        for (DateOnly day = gridStart; day <= gridEnd; day = day.AddDays(1))
        {
            double seconds = secondsByDay.GetValueOrDefault(Key(day));
            days.Add(new UsageHeatmapDay(day, seconds, OpacityFor(seconds)));
        }

        // 天数就是列数的出处（七天一列，进位留一列短的）；还没量到宽时 0 天 → 0 列，两行标签跟着空。
        columns = (days.Count + DaysPerColumn - 1) / DaysPerColumn;

        // 两行标签都按列铺一枚（不该亮的空串）：列数与格子那台同源，三台的列才会一格对一格。
        // 年份行常亮（2026-10-22 口述"即使视图内只有一年，也要显示年份"）；月份行每月 1 号照报，
        // 1 月不再让位给年份行（同日口述"少了 1 月的显示"）——年份压在上面，两行各说各的。
        var monthLabels = new List<UsageHeatmapMonthLabel>(columns);
        var yearLabels = new List<UsageHeatmapYearLabel>(columns);
        for (int k = 0; k < columns; k++)
        {
            monthLabels.Add(new UsageHeatmapMonthLabel(MonthLabelFor(days, k)));
            yearLabels.Add(new UsageHeatmapYearLabel(YearLabelFor(days, k)));
        }

        // 列数进指纹：拖动窗口只改宽度、不改列数时这一发整张图空转，一根格子都不重建。
        string stamp = year.ToString(CultureInfo.InvariantCulture) + "|" + columns.ToString(CultureInfo.InvariantCulture)
            + "|" + Key(today)
            + "|" + secondsByDay.Count.ToString(CultureInfo.InvariantCulture)
            + "|" + total.ToString("F0", CultureInfo.InvariantCulture)
            + "|" + (earliest is null ? "-" : Key(earliest.Value))
            + "|" + (UsageStore.RecordRejected ? "1" : "0");
        return new HeatmapSnapshot(year, columns, days, monthLabels, yearLabels,
            years, summary, note, noteVisibility, stamp);
    }

    private sealed record HeatmapSnapshot(
        int Year,
        int Columns,
        List<UsageHeatmapDay> Days,
        List<UsageHeatmapMonthLabel> MonthLabels,
        List<UsageHeatmapYearLabel> YearLabels,
        List<int> Years,
        string Summary,
        string Note,
        Visibility NoteVisibility,
        string Stamp);

    /// <summary>第 <paramref name="column"/> 列下面那枚月份标签：这一列里（最多七天）撞上了某个月的 1 号就报"N月"。
    /// <b>1 月也照报</b>（2026-10-22 口述"少了 1 月的显示"）——年份已经由上面那一行常亮的年份标签说了，
    /// 两行各说各的，月份行不再让位。左边补出来的那段也是真日子，月份跟着走才对得上格子 tooltip 里的日期。</summary>
    private static string MonthLabelFor(List<UsageHeatmapDay> days, int column)
    {
        int from = column * DaysPerColumn;
        int to = Math.Min(from + DaysPerColumn, days.Count);
        for (int i = from; i < to; i++)
        {
            DateOnly date = days[i].Date;
            if (date.Day == 1)
            {
                return date.Month + "月";
            }
        }

        return string.Empty;
    }

    /// <summary>第 <paramref name="column"/> 列上面那枚年份标签：整行<b>常亮</b>（2026-10-22 口述
    /// "即使视图内只有一年，也要显示年份"）。亮法照官方"年份压在一年的开头"：
    /// <b>最左那一列</b>报它第一天所在的年（它前面没东西了，得先有个起头），
    /// 之后哪一列里<b>撞上了 1 月 1 号</b>就在这列报那一年——年界多半落在某一列的中途（列是"往前数七天"，
    /// 不要求从周日起），所以判据只能是"这列里有没有 Jan 1"，不能是"这列开头换年了没有"
    /// （照后者实测会漏掉整条带上唯一那个新年头，2026-10-22 用 2025-11-07→2026-10-22 这条带验出来）。
    /// 其余列一律空串。</summary>
    private static string YearLabelFor(List<UsageHeatmapDay> days, int column)
    {
        int from = column * DaysPerColumn;
        if (from >= days.Count)
        {
            return string.Empty;
        }

        if (column == 0)
        {
            return days[from].Date.Year + "年";
        }

        int to = Math.Min(from + DaysPerColumn, days.Count);
        for (int i = from; i < to; i++)
        {
            DateOnly date = days[i].Date;
            if (date.Month == 1 && date.Day == 1)
            {
                return date.Year + "年";
            }
        }

        return string.Empty;
    }

    /// <summary>五档按“当天累计分钟数”分：0 / &lt;10 / &lt;30 / &lt;60 / 60+，报成强调色那层的浓淡。</summary>
    private static double OpacityFor(double seconds) => seconds switch
    {
        <= 0 => 0,
        < 600 => 0.25,
        < 1800 => 0.5,
        < 3600 => 0.75,
        _ => 1,
    };

    /// <summary>账本里存的是秒，界面按分钟/小时说话；不足一分钟给秒，避免“看了 0 分钟”。</summary>
    public static string DurationText(double seconds)
    {
        long total = (long)Math.Round(seconds);
        if (total < 60)
        {
            return Math.Max(total, 1) + " 秒";
        }

        long minutes = (total + 30) / 60;
        if (minutes < 60)
        {
            return minutes + " 分钟";
        }

        long hours = minutes / 60;
        long rest = minutes % 60;
        return rest == 0 ? hours + " 小时" : hours + " 小时 " + rest + " 分钟";
    }

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void NotifyAll()
    {
        foreach (string name in Names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static readonly string[] Names =
    [
        nameof(Days), nameof(MonthLabels), nameof(YearLabels), nameof(ColumnCount), nameof(StripWidth),
        nameof(Year), nameof(YearIndex), nameof(Summary), nameof(Note), nameof(NoteVisibility),
    ];
}
