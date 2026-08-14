namespace WinQuota.Service.Services;

public readonly record struct ProcessSnapshot(int Pid, string ProcessName, string? ExecutablePath);
