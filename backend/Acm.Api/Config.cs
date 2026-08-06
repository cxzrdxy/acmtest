namespace Acm.Api;

/// <summary>JWT 签发配置，绑定 appsettings "Jwt" 节。</summary>
public class JwtOptions
{
    public string SecretKey { get; set; } = null!;
    public string Issuer { get; set; } = "acm-oj";
    public int AccessTtlMinutes { get; set; } = 60 * 24 * 7;   // 7 天，自用可设长
}

/// <summary>应用级开关，绑定 appsettings "App" 节。</summary>
public class AppOptions
{
    // 是否开放自助注册；个人自用可在创建账号后关闭防止他人注册
    public bool AllowRegister { get; set; } = true;
    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];
}

/// <summary>评测配置，绑定 appsettings "Judge" 节。</summary>
public class JudgeOptions
{
    // 测试点根目录（宿主路径）；M2.4 测试点管理使用，此处先定义
    public string TestcaseRoot { get; set; } = null!;
    // true = Docker 沙箱执行；false = 宿主进程直跑（仅开发调试，Windows 下无沙箱）
    public bool UseDockerSandbox { get; set; } = true;
    public string DockerImageCpp { get; set; } = "judge-cpp:latest";
    public string DockerImagePython { get; set; } = "judge-python:latest";
    // 用户程序输出上限（字节），超出判 RE
    public long OutputLimitBytes { get; set; } = 16 * 1024 * 1024;
}
