
using WinDouYin.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinDouYin.Controls;

/// <summary>
/// 透明手势层专用：悬停时显示"可点击"的手形光标。
/// <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/> 是 protected，只能由子类在内部设；
/// 这里用 Grid（Border 是 sealed），光标只落在视频区，不会把按钮也变成手形。
/// </summary>
public sealed partial class HandCursorLayer : Grid
{
    /// <summary>光标对象留一份静态引用：只靠 ProtectedCursor 持有时，GC 时机不归我们管。</summary>
    private static readonly InputSystemCursor HandCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Hand);

    public HandCursorLayer()
    {
        ProtectedCursor = HandCursor;
    }
}
