using Microsoft.EntityFrameworkCore;
using Acm.Judge.Core.Data;
using Acm.Api.Dtos;
using Acm.Judge.Core.Models;

namespace Acm.Api.Services;

/// <summary>业务错误统一抛此异常，由全局异常处理器转 {detail: msg}。</summary>
public class ApiException(int status, string detail) : Exception(detail)//自定义异常类，继承 Exception，detail 传给父类 Exception
{
    public int Status { get; } = status;// 只读属性，表示 HTTP 状态码
}

public class AuthService(AppDbContext db)//用户服务封装
{
    public async Task<User> RegisterAsync(RegisterRequest req)//声明注册异步任务的封装
    {
        if (await db.Users.AnyAsync(u => u.Username == req.Username))//判断用户名是否已存在
            throw new ApiException(409, "用户名已存在");

        var user = new User // ① 在内存中创建 User 对象（此时还没进数据库）
        { 
            Username = req.Username, // ② 把前端传来的用户名存进对象
            HashedPassword = BCrypt.Net.BCrypt.HashPassword( // ③ 对密码做 bcrypt 哈希
                req.Password), 
        }; 
        db.Users.Add(user); // ④ 标记：把 user 加入 DbContext 跟踪器，状态 = 待插入
        await db.SaveChangesAsync(); // ⑤ 提交：把标记的插入翻译成 INSERT SQL 执行入库
        return user; // ⑥ 返回带 Id 的用户对象（SaveChanges 后 EF 已回填 Id）
    }

    public async Task<User> LoginAsync(LoginRequest req)//登录操作
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == req.Username);
        // 个人自用：使用统一报错信息（不做防枚举差异化处理）
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.HashedPassword))
            throw new ApiException(401, "用户名或密码错误");
        if (!user.IsActive)
            throw new ApiException(403, "账号已停用");
        return user;
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest req)//修改密码操作
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        if (!BCrypt.Net.BCrypt.Verify(req.OldPassword, user.HashedPassword))
            throw new ApiException(400, "旧密码错误");
        user.HashedPassword = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await db.SaveChangesAsync();
    }
}
