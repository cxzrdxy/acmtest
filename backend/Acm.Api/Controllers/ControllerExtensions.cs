using System.Security.Claims; // Claim 类型：JWT 里承载的用户声明（sub 等）
using Microsoft.AspNetCore.Mvc; // ControllerBase：所有控制器的基类（扩展方法作用对象）

namespace Acm.Api.Controllers;

public static class ControllerExtensions // 控制器扩展方法集合（静态类）
{
    /// <summary>取当前用户 ID（来自 JWT 的 sub claim，中间件映射为 NameIdentifier）。</summary>
    public static int CurrentUserId(this ControllerBase c) => // 扩展方法：this 前缀 = 给 ControllerBase 加方法，控制器可直接 this.CurrentUserId() 调用
        int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!); // ①c.User=JWT解析出的身份 ②FindFirstValue取sub声明(userId) ③int.Parse转数字返回
}
