using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Acm.Api.Controllers;

public static class ControllerExtensions
{
    /// <summary>取当前用户 ID（来自 JWT 的 sub claim，中间件映射为 NameIdentifier）。</summary>
    public static int CurrentUserId(this ControllerBase c) =>
        int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
