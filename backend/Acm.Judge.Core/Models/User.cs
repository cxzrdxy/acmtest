namespace Acm.Judge.Core.Models;

public class User
{
    public int Id { get; set; }                          // 用户 ID，自增主键
    public string Username { get; set; } = null!;        // 登录名，唯一索引，最长 32
    public string HashedPassword { get; set; } = null!;  // bcrypt 哈希后的密码
    public bool IsActive { get; set; } = true;           // 软停用标记（自用通常恒为 true）
    public DateTime CreatedAt { get; set; }              // 注册时间，DB 默认 now()
}
