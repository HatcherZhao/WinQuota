namespace WinQuota.Service.Services;

public interface INotifier
{
    /// <summary>向当前登录用户展示一条通知。Windows 服务运行在会话 0 中，需要借助用户会话进程展示。</summary>
    void Notify(string title, string message);
}
