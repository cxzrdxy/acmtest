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
