using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using WinDouYin.Contracts;
using WinDouYin.ViewModels;
using WinDouYin.Views;

namespace WinDouYin.Controls;
/// 账号菜单 →「消息」右侧面板：会话列表 → 点进去是聊天窗（历史/翻页/发送），
/// 输入条上一枚表情键开官方表情与官方表情包。写链的材料来自登录时捞的 guard 材料。
/// </summary>
public sealed partial class MessagesPanel : UserControl
{
    private const int EmojiPerRow = 8;
    private const int EmojiSize = 18;
    private const int EmojiAscent = 14;

    /// <summary>表情标签字符的起点：输入框里图后面跟一个 1px 透明字符，取正文时还原成码。</summary>
    private const char EmojiTagBase = '\uE000';

    private bool _emojiBuilt;
    private bool _sending;

    /// <summary>当前正放着落点高亮的那一个列表项容器（换会话、关面板、重铺列表时都要掐回 Normal，免得留着半截动画）。</summary>
    private ListViewItem? _flashedItem;

    public MessagesPanel()
    {
        InitializeComponent();
    }

    public MessagesViewModel Messages { get; } = new();

    public event EventHandler? CloseRequested;

    /// <summary>点开聊天里的分享卡：把视频 id 与可选的评论上下文交给宿主去取详情/开详情页。</summary>
    public event EventHandler<ShareOpenEventArgs>? ShareOpenRequested;

    public async Task OpenAsync()
    {
        PickerFlyout.Hide();
        await Messages.LoadAsync();
        _ = PrefetchPickerAsync();
    }

    /// <summary>面板一开就把表情表/表情包取上（用户真去点表情时不再等）。失败就等打开时重试。</summary>
    private async Task PrefetchPickerAsync()
    {
        try
        {
            await Messages.EnsureEmojisAsync();
        }
        catch
        {
        }

        try
        {
            if (Douyin.Feed.IsLoggedIn)
            {
                await Messages.EnsureStickersAsync();
                await Messages.EnsureFavoritesAsync();
            }
        }
        catch
        {
        }
    }

    /// <summary>收起面板：聊天窗也一并收掉，下次打开回到会话列表。</summary>
    public void Close()
    {
        PickerFlyout.Hide();
        StopFlash();
        Messages.CloseChat();
        ClearInput();
    }

    public void Reset()
    {
        PickerFlyout.Hide();
        StopFlash();
        ClearInput();
        Messages.Reset();
    }

    private async void OnConversationClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MessageRow row)
        {
            return;
        }

        PickerFlyout.Hide();
        StopFlash();
        await Messages.OpenChatAsync(row);
        ScrollChatToEnd();
    }

    private void OnChatBackClick(object sender, RoutedEventArgs e)
    {
        PickerFlyout.Hide();
        StopFlash();
        Messages.CloseChat();
    }

    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0)
        {
            return;
        }

        // 视频列表取当前会话里已加载的分享卡（按聊天顺序、同一条视频只留一次），
        // 带着它进详情页，上一/下一就在这组上下文里走。
        var context = new List<SharedVideoRef>();
        SharedReviewArgs? review = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChatMessageRow row in Messages.ChatRows)
        {
            if (row.ShareVideoId.Length == 0 || !seen.Add(row.ShareVideoId))
            {
                continue;
            }

            context.Add(new SharedVideoRef(row.ShareVideoId, row.ReviewArgs));
            if (row.ShareVideoId == id)
            {
                review = row.ReviewArgs;
            }
        }

        ShareOpenRequested?.Invoke(this, new ShareOpenEventArgs(id, review, context));
    }

    private void OnPanelCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnOlderClick(object sender, RoutedEventArgs e) =>
        await Messages.LoadOlderAsync();

    /// <summary>点引用：跳到被回复那条（没加载就先往上翻几页），并把落点那一行淡入淡出高亮一下。</summary>
    private async void OnQuoteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessageRow source })
        {
            return;
        }

        ChatMessageRow? target = await Messages.LocateQuotedRowAsync(source);
        if (target is null)
        {
            // 找不到时 ViewModel 已经把话写在发送状态那一行。
            return;
        }

        // 翻页后整表重建，给系统一拍把容器实例化出来再滚、再高亮（与 ScrollChatToEnd 同口径）。
        DispatcherQueue.TryEnqueue(() =>
        {
            ChatList.ScrollIntoView(target);
            FlashRow(target);
        });
    }

    /// <summary>
    /// 高亮落点那一行：动画（淡入 180ms → 停 1.1s → 淡出 700ms）整个写在
    /// <c>ChatList.ItemContainerStyle</c> 的 <c>Flashed</c> 状态里，这里只把那个容器从 Normal 切到 Flashed。
    /// 状态挂在列表项容器上而不是会话行里，是因为 WinUI 3 的 <see cref="VisualStateManager.GoToState"/>
    /// 只接受 <see cref="Control"/>（包内 metadata 实测只有 GoToState(Control,…)、没有 UWP 的
    /// GoToElementState），而会话行的根是 Grid；<see cref="ListViewItem"/> 本身就是 Control。
    /// 先落一次 Normal：已经在 Flashed 的容器再切一次不会重播。
    /// </summary>
    private void FlashRow(ChatMessageRow target)
    {
        StopFlash();

        if (ChatList.ContainerFromItem(target) is not ListViewItem item)
        {
            return;
        }

        VisualStateManager.GoToState(item, "Normal", false);
        VisualStateManager.GoToState(item, "Flashed", true);
        _flashedItem = item;
    }

    /// <summary>掐掉落点高亮：切回 Normal 时那条动画会被系统停掉，Opacity 也就回到模板里写死的 0。</summary>
    private void StopFlash()
    {
        if (_flashedItem is not null)
        {
            VisualStateManager.GoToState(_flashedItem, "Normal", false);
            _flashedItem = null;
        }
    }

    /// <summary>表情面板挂在输入框上（以整条输入框为锚点，弹层水平居中）；按钮只负责开合。</summary>
    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (PickerFlyout.IsOpen)
        {
            PickerFlyout.Hide();
            return;
        }

        FlyoutBase.ShowAttachedFlyout(ChatInput);
    }

    /// <summary>Flyout 打开：按当前选中的页签刷新（表情表建一次，表情包按 tab 取）。</summary>
    private async void OnPickerFlyoutOpened(object? sender, object e)
    {
        await RefreshPickerAsync();
    }

    private bool _pickerRefreshQueued;

    private void OnPickerTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (PickerFlyout is null || !PickerFlyout.IsOpen)
        {
            return;
        }

        // SelectorBar 抛 SelectionChanged 时，item 的选中态可能还没落定；挪到下一拍再读，
        // 否则第一下点击会按旧页签刷新（表现为要来回切一次才跳对）。
        QueuePickerRefresh();
    }

    /// <summary>兜底：个别情况下第一次点击只落到焦点、没提交选择；这里把被点的页签补成选中。</summary>
    private void OnPickerTabItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (PickerFlyout is null || !PickerFlyout.IsOpen)
        {
            return;
        }

        if (sender is SelectorBarItem item && !item.IsSelected)
        {
            item.IsSelected = true;
            QueuePickerRefresh();
        }
    }

    /// <summary>把同一拍里的多次页签点击并成一次刷新，刷新时读最终选中的页签。</summary>
    private void QueuePickerRefresh()
    {
        if (_pickerRefreshQueued)
        {
            return;
        }

        _pickerRefreshQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            _pickerRefreshQueued = false;
            if (PickerFlyout is null || !PickerFlyout.IsOpen)
            {
                return;
            }

            await RefreshPickerAsync();
        });
    }

    private async Task RefreshPickerAsync()
    {
        bool stickers = PickerTabs.SelectedItem is null
            ? StickerTab.IsSelected
            : PickerTabs.SelectedItem as SelectorBarItem == StickerTab;
        EmojiPane.Visibility = stickers ? Visibility.Collapsed : Visibility.Visible;
        StickerPane.Visibility = stickers ? Visibility.Visible : Visibility.Collapsed;
        if (stickers)
        {
            await Messages.ShowStickerPaneAsync(SelectedStickerPane());
        }
        else
        {
            await ShowEmojiAsync();
        }
    }

    private StickerSource SelectedStickerPane()
    {
        if (StickerTabs is null)
        {
            return StickerSource.Trending;
        }

        // 按 `SelectorBarItem?` 收一手再比：`SelectedItem` 是 object，直接拿它和具体 item 比，
        // C# 会报 CS0252「可能非有意的引用比较」——这里要的正是引用比较（同一枚 item 实例），
        // 把左边收成同类型就把这层意思写实了。
        SelectorBarItem? selected = StickerTabs.SelectedItem as SelectorBarItem;
        if (selected == RecentStickerTab)
        {
            return StickerSource.Recent;
        }

        if (selected == FavoriteStickerTab)
        {
            return StickerSource.Favorite;
        }

        if (selected == SearchStickerTab)
        {
            return StickerSource.Search;
        }

        return StickerSource.Trending;
    }

    private void OnStickerTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (PickerFlyout is null || !PickerFlyout.IsOpen || StickerPane is null || StickerPane.Visibility != Visibility.Visible)
        {
            return;
        }

        // 同上：等 SelectorBar 的选中态落定后再按 SelectedItem 取来源。
        QueuePickerRefresh();
    }

    private async void OnStickerMoreClick(object sender, RoutedEventArgs e) =>
        await Messages.LoadMoreStickersAsync();

    private async void OnStickerSearchClick(object sender, RoutedEventArgs e) =>
        await Messages.SearchStickersAsync(StickerSearchBox.Text);

    private async void OnStickerSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await Messages.SearchStickersAsync(StickerSearchBox.Text);
    }

    private async void OnStickerClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }
            && Messages.StickerItems.FirstOrDefault(s => s.Id == id) is { } item)
        {
            await SendStickerAsync(item.Sticker);
        }
    }

    private async void OnStickerFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string id })
        {
            await Messages.ToggleStickerFavoriteAsync(id);
        }
    }

    private async Task ShowEmojiAsync()
    {
        if (_emojiBuilt)
        {
            SetPickerStatus(string.Empty);
            return;
        }

        SetPickerStatus("正在读取官方表情…");
        try
        {
            IReadOnlyList<EmojiItem> list = await Messages.EnsureEmojisAsync();
            if (list.Count == 0)
            {
                SetPickerStatus("表情表是空的");
                return;
            }

            BuildEmojiRows(list);
            _emojiBuilt = true;
            SetPickerStatus(string.Empty);
        }
        catch (Exception ex)
        {
            SetPickerStatus("表情读取失败：" + ex.Message);
        }
    }

    private void BuildEmojiRows(IReadOnlyList<EmojiItem> list)
    {
        EmojiHost.Children.Clear();
        var rows = new StackPanel { Spacing = 2 };
        StackPanel? row = null;
        for (int i = 0; i < list.Count; i++)
        {
            if (i % EmojiPerRow == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                rows.Children.Add(row);
            }

            EmojiItem emoji = list[i];
            var button = new Button
            {
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new Image
                {
                    Width = 24,
                    Height = 24,
                    Source = new BitmapImage(new Uri(emoji.ImageUrl)),
                },
            };
            ToolTipService.SetToolTip(button, emoji.Code);
            button.Click += async (_, _) => await InsertEmojiAsync(emoji);
            row!.Children.Add(button);
        }

        EmojiHost.Children.Add(rows);
    }

    private void SetPickerStatus(string text)
    {
        PickerStatus.Text = text;
        PickerStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 插一个官方表情（真图片 + 透明标签）。图先转 PNG——RichEditBox.InsertImage 不认图床给的 webp
    /// （见 <see cref="EmojiImage"/>）。
    /// </summary>
    private async Task InsertEmojiAsync(EmojiItem emoji)
    {
        int index = EmojiCatalog.IndexOf(emoji);
        if (index < 0 || index > char.MaxValue - EmojiTagBase)
        {
            return;
        }

        byte[]? png = await EmojiImage.LoadPngAsync(emoji.ImageUrl);
        if (png is null)
        {
            SetPickerStatus("这个表情的图取不到");
            return;
        }

        try
        {
            ITextSelection selection = ChatInput.Document.Selection;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            selection.InsertImage(EmojiSize, EmojiSize, EmojiAscent, VerticalCharacterAlignment.Baseline, emoji.Code, stream);

            selection.SetText(TextSetOptions.None, ((char)(EmojiTagBase + index)).ToString());
            selection.CharacterFormat.Size = 1;
            selection.CharacterFormat.ForegroundColor = Microsoft.UI.Colors.Transparent;
            selection.Collapse(false);

            ITextCharacterFormat normal = ChatInput.Document.GetDefaultCharacterFormat();
            selection.CharacterFormat.Size = normal.Size;
            selection.CharacterFormat.ForegroundColor = normal.ForegroundColor;

            ChatInput.Focus(FocusState.Programmatic);
            UpdateSendState();
        }
        catch (Exception ex)
        {
            SetPickerStatus("这个表情插不进去：" + ex.Message);
        }
    }

    /// <summary>取输入框正文：标签字符还原成 [码]，图片占位符与段落符丢掉。</summary>
    private static string ChatBoxText(RichEditBox box)
    {
        box.Document.GetText(TextGetOptions.None, out string raw);
        var text = new System.Text.StringBuilder();
        foreach (char c in raw)
        {
            int index = c - EmojiTagBase;
            string code = EmojiCatalog.CodeAt(index);
            if (code.Length > 0)
            {
                text.Append(code);
                continue;
            }

            if (c is '\r' or '\n' or '\uFFFC')
            {
                continue;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private void OnChatInputTextChanged(object sender, RoutedEventArgs e) => UpdateSendState();

    private async void OnChatInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (shift)
        {
            return;   // Shift+Enter 换行
        }

        e.Handled = true;
        await SendAsync();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (_sending)
        {
            return;
        }

        string text = ChatBoxText(ChatInput).Trim();
        if (text.Length == 0)
        {
            return;
        }

        _sending = true;
        UpdateSendState();
        bool ok = await Messages.SendTextAsync(text);
        if (ok)
        {
            ClearInput();
            PickerFlyout.Hide();
            ScrollChatToEnd();
        }

        _sending = false;
        UpdateSendState();
    }

    private async Task SendStickerAsync(ImSticker sticker)
    {
        if (_sending)
        {
            return;
        }

        _sending = true;
        UpdateSendState();
        bool ok = await Messages.SendStickerAsync(sticker);
        if (ok)
        {
            PickerFlyout.Hide();
            ScrollChatToEnd();
        }

        _sending = false;
        UpdateSendState();
    }

    private void ClearInput()
    {
        ChatInput.Document.SetText(TextSetOptions.None, string.Empty);
        UpdateSendState();
    }

    private void UpdateSendState()
    {
        ChatInput.Document.GetText(TextGetOptions.None, out string raw);
        bool hasContent = raw.Replace("\r", string.Empty).Replace("\n", string.Empty).Length > 0;
        SendButton.IsEnabled = !_sending && hasContent;
    }

    private void ScrollChatToEnd()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Messages.ChatRows.Count > 0)
            {
                ChatList.ScrollIntoView(Messages.ChatRows[^1]);
            }
        });
    }
}

/// <summary>按消息种类挑模板：系统提示居中，其余消息都走同一条会话行（对方在左、自己在右）。</summary>
public sealed partial class ChatTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MessageTemplate { get; set; }

    public DataTemplate? SystemTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) =>
        item is ChatMessageRow { IsSystem: true }
            ? SystemTemplate ?? MessageTemplate!
            : MessageTemplate!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
