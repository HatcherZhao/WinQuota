namespace WinQuota.Service.Services;

public interface INotifier
{
    /// <summary>向当前登录用户展示一条通知。Windows 服务运行在会话 0 中，需要借助用户会话进程展示。</summary>
    void Notify(string title, string message);

    /// <summary>
    /// 关键通知（延期宽限等必须在全屏游戏中可见的场景）：
    /// Toast 之外同时弹 msg.exe 置顶消息框——全屏游戏会触发专注助手压制 Toast 横幅，
    /// msg 弹窗可以穿透显示。
    /// </summary>
    void NotifyCritical(string title, string message);
}
