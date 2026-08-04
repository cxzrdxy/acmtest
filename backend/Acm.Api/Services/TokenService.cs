using System.IdentityModel.Tokens.Jwt; // JWT 签发相关类型（JwtSecurityToken / 处理器）
using System.Security.Claims; // Claim 类型：JWT 里承载的用户声明
using System.Text; // Encoding.UTF8：把密钥字符串转成字节数组
using Microsoft.Extensions.Options; // IOptions<JwtOptions>：读取 Jwt 配置
using Microsoft.IdentityModel.Tokens; // 密钥/签名相关类型（SymmetricSecurityKey 等）

namespace Acm.Api.Services;

public class TokenService(IOptions<JwtOptions> opt) // JWT 签发服务，主构造函数注入配置
{
    private readonly JwtOptions _jwt = opt.Value; // 取配置值缓存到字段：密钥/签发者/过期时间

    public string CreateAccessToken(int userId) // 签发 token：传入用户 Id，返回 JWT 字符串
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey)); // ① 用配置的密钥字符串建对称加密密钥对象
        var token = new JwtSecurityToken( // ② 构造 JWT 对象（先建"待签名的内容"，还没转字符串）
            issuer: _jwt.Issuer, // 签发者：acm-oj（验证端会核对）
            claims: [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], // 声明：sub = 用户 Id（验证端据此识别用户）
            expires: DateTime.UtcNow.AddMinutes(_jwt.AccessTtlMinutes), // 过期时间：当前时间 + 配置的 7 天
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)); // 签名凭据：用 key 做 HMAC-SHA256 签名防篡改
        return new JwtSecurityTokenHandler().WriteToken(token); // ③ 处理器把 JWT 对象序列化成字符串，返回给调用方
    }
}
