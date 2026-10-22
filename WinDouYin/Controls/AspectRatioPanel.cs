
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinDouYin.Controls;

/// <summary>
/// 一个"宽定高"的容器：宽度吃满父级，高度按 <see cref="Ratio"/>（高/宽）算。
/// 精选卡封面是 16:9（官方 <c>padding-top:56.25%</c> 那套），而 WinUI 的 Image 只按源图比例长，
/// 不给我们"宽度决定高度"；封面的源图又常是 1:1，直接摆会拉成方块。
/// 这个面板只管量出 16:9 的框，里面 Image 用 UniformToFill 裁切——和官方服务端裁切后的观感一致。
/// </summary>
public sealed partial class AspectRatioPanel : Panel
{
    /// <summary>高 / 宽。默认 9/16（视频封面）。</summary>
    public double Ratio { get; set; } = 9.0 / 16.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : 300;
        double height = width * Ratio;
        var size = new Size(width, height);
        foreach (UIElement child in Children)
        {
            child.Measure(size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(0, 0, finalSize.Width, finalSize.Height);
        foreach (UIElement child in Children)
        {
            child.Arrange(rect);
        }

        return finalSize;
    }
}
