using Windows.ApplicationModel.DataTransfer;

namespace WinDouYin.Service;

/// <summary>
/// Windows 系统分享（桌面应用要走 DataTransferManagerInterop 绑定窗口句柄）。
/// 分享内容暂存静态字段、处理器用静态方法并在触发后退订，避免长期根住某个舞台控件。
/// </summary>
internal static class SystemShare
{
    private static DataTransferManager? _manager;
    private static string _title = string.Empty;
    private static string _link = string.Empty;
    private static string _description = string.Empty;

    public static void Show(IntPtr hwnd, string title, string link, string description)
    {
        _title = title;
        _link = link;
        _description = description;

        _manager = DataTransferManagerInterop.GetForWindow(hwnd);
        _manager.DataRequested -= OnDataRequested;
        _manager.DataRequested += OnDataRequested;
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
    }

    private static void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        sender.DataRequested -= OnDataRequested;

        DataPackage data = args.Request.Data;
        data.Properties.Title = _title;
        data.Properties.Description = _description;
        data.SetText(_title + "\r\n" + _link);
        try
        {
            data.SetWebLink(new Uri(_link));
        }
        catch (UriFormatException)
        {
            // 链接格式不对时至少还有文本
        }
    }
}
