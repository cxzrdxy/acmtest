using Microsoft.EntityFrameworkCore;
using Acm.Api.Models;

namespace Acm.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)//整个项目唯一的数据库上下文
{
    public DbSet<User> Users => Set<User>();//user表的入口声明
    public DbSet<Problem> Problems => Set<Problem>();//problem表的入口声明

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>//把 C# 实体类 User 和数据库表 users 关联起来，并定义表结构约束
        {
            e.ToTable("users"); // 指定数据库表名为 users（不加则默认用属性名 Users）
            e.HasIndex(u => u.Username).IsUnique(); // 给 username 列建唯一索引（注册查重走它，重复插入会被数据库拒绝）
            e.Property(u => u.Username).HasMaxLength(32); // username 列最长 32 字符（数据库层强制）
            e.Property(u => u.HashedPassword).HasMaxLength(256); // 密码哈希列最长 256 字符
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()"); // 插入时不填 CreatedAt，数据库自动填当前时间
        });

        b.Entity<Problem>(e =>
        {
            e.ToTable("problems"); // 指定数据库表名为 problems
            e.HasIndex(p => p.Slug).IsUnique(); // slug 列唯一索引（P1000 之类，by-slug 查询走它）
            e.Property(p => p.Slug).HasMaxLength(64); // slug 列最长 64 字符
            e.Property(p => p.Title).HasMaxLength(128); // 标题列最长 128 字符
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()"); // 创建时间默认当前时间
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()"); // 更新时间默认当前时间（业务层 UpdateAsync 里也会手动刷新）
            e.HasOne<User>().WithMany().HasForeignKey(p => p.AuthorId); // 外键：problems.author_id → users.id（题目属于哪个用户）
        });
    }
}
