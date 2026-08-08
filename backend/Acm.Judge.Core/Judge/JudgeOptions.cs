namespace Acm.Judge.Core.Judge;

/// <summary>评测配置，绑定 appsettings "Judge" 节（Web/Worker 共用）。</summary>
public class JudgeOptions
{
    // 测试点根目录（宿主路径）
    public string TestcaseRoot { get; set; } = null!;
    // true = Docker 沙箱执行；false = 宿主进程直跑（仅开发调试，Windows 下无沙箱）
    public bool UseDockerSandbox { get; set; } = true;
    public string DockerImageCpp { get; set; } = "judge-cpp:latest";
    public string DockerImagePython { get; set; } = "judge-python:latest";
    // 用户程序输出上限（字节），超出截断
    public long OutputLimitBytes { get; set; } = 16 * 1024 * 1024;
}
