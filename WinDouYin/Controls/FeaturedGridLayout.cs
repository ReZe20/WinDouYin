
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinDouYin.Controls;

/// <summary>
/// 精选网格：CSS Grid 那条"首卡占 3 列 × 2 行、其余按列优先填满"的账，在 WinUI 里没有现成布局，
/// 这里自己排（不虚拟化：一页 20 条、上限 240，ItemsRepeater 的元素会全部实现）。
///
/// 口径全部来自 2026-10-21 官方 1600×1000 实测：
/// <list type="bullet">
/// <item>列宽下限 310；列距 16、行距 24；内容宽 ≥962 三列、≥1288 四列、≥1614 五列；</item>
/// <item>视口 ≥1536（内容宽约 1332）才起大卡：首卡宽 = 3 列 + 2 个列距，16:9；</item>
/// <item>大卡右侧那一列先竖排两枚小卡（5 列时是右侧两列各两枚），再回到整行；</item>
/// <item>小卡高 = 封面 16:9 + 信息块 72（标题两行 40 + 作者行 20 + 间距）。</item>
/// </list>
/// 大卡高度按 16:9 自己算，不硬凑 2×小卡 + 行距（实测两者只差 1~2px；差出来的那点留在右列底下）。
/// </summary>
public sealed partial class FeaturedGridLayout : NonVirtualizingLayout
{
    public const double MinItemWidth = 310;
    public const double ColumnGap = 16;
    public const double RowGap = 24;
    public const double InfoHeight = 72;
    public const double HeroMinWidth = 1332;
    public const int HeroColumnSpan = 3;

    public static int ColumnsFor(double width)
    {
        if (width >= 1614)
        {
            return 5;
        }

        if (width >= 1288)
        {
            return 4;
        }

        if (width >= 962)
        {
            return 3;
        }

        return width >= 636 ? 2 : 1;
    }

    /// <summary>大卡只在"四列以上 + 内容宽够"时出现；窄了退回统一网格（官方 1180 实测就是 3 列无大卡）。</summary>
    public static bool HeroFor(double width) => width >= HeroMinWidth && ColumnsFor(width) >= 4;

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : MinItemWidth;
        (_, double cellWidth, double cardHeight, double heroWidth, double heroHeight, bool hero) = Metrics(width);

        IReadOnlyList<UIElement> children = context.Children;
        for (int i = 0; i < children.Count; i++)
        {
            children[i].Measure(hero && i == 0
                ? new Size(heroWidth, heroHeight)
                : new Size(cellWidth, cardHeight));
        }

        return new Size(width, HeightFor(children.Count, width));
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        double width = finalSize.Width;
        (int columns, double cellWidth, double cardHeight, double heroWidth, double heroHeight, bool hero) = Metrics(width);
        IReadOnlyList<UIElement> children = context.Children;
        if (children.Count == 0)
        {
            return finalSize;
        }

        if (!hero)
        {
            for (int i = 0; i < children.Count; i++)
            {
                int row = i / columns;
                int column = i % columns;
                children[i].Arrange(new Rect(
                    column * (cellWidth + ColumnGap),
                    row * (cardHeight + RowGap),
                    cellWidth,
                    cardHeight));
            }

            return finalSize;
        }

        children[0].Arrange(new Rect(0, 0, heroWidth, heroHeight));
        double bandHeight = Math.Max(heroHeight, 2 * cardHeight + RowGap);
        int sideColumns = columns - HeroColumnSpan;
        int bandSlots = 2 * sideColumns;
        int bandUsed = Math.Min(children.Count - 1, bandSlots);
        for (int k = 0; k < bandUsed; k++)
        {
            int row = k / sideColumns;
            int column = HeroColumnSpan + (k % sideColumns);
            children[1 + k].Arrange(new Rect(
                column * (cellWidth + ColumnGap),
                row == 0 ? 0 : cardHeight + RowGap,
                cellWidth,
                cardHeight));
        }

        int start = 1 + bandUsed;
        for (int i = start; i < children.Count; i++)
        {
            int t = i - start;
            int row = t / columns;
            int column = t % columns;
            children[i].Arrange(new Rect(
                column * (cellWidth + ColumnGap),
                bandHeight + RowGap + row * (cardHeight + RowGap),
                cellWidth,
                cardHeight));
        }

        return finalSize;
    }

    private static (int Columns, double CellWidth, double CardHeight, double HeroWidth, double HeroHeight, bool Hero)
        Metrics(double width)
    {
        int columns = ColumnsFor(width);
        double cellWidth = Math.Max(1, (width - (columns - 1) * ColumnGap) / columns);
        double cardHeight = cellWidth * 9.0 / 16.0 + InfoHeight;
        bool hero = HeroFor(width);
        double heroWidth = HeroColumnSpan * cellWidth + (HeroColumnSpan - 1) * ColumnGap;
        double heroHeight = heroWidth * 9.0 / 16.0;
        return (columns, cellWidth, cardHeight, heroWidth, heroHeight, hero);
    }

    private static double HeightFor(int count, double width)
    {
        if (count <= 0)
        {
            return 0;
        }

        (int columns, _, double cardHeight, _, double heroHeight, bool hero) = Metrics(width);
        if (!hero)
        {
            int rows = (count + columns - 1) / columns;
            return rows * cardHeight + (rows - 1) * RowGap;
        }

        double bandHeight = Math.Max(heroHeight, 2 * cardHeight + RowGap);
        int tail = count - 1 - Math.Min(count - 1, 2 * (columns - HeroColumnSpan));
        if (tail <= 0)
        {
            return bandHeight;
        }

        int tailRows = (tail + columns - 1) / columns;
        return bandHeight + RowGap + tailRows * cardHeight + (tailRows - 1) * RowGap;
    }
}
