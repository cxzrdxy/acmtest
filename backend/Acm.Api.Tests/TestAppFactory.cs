using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Acm.Api.Data;

namespace Acm.Api.Tests;

/// <summary>内存起站点；连同一个 Postgres，测试前 TRUNCATE 表做隔离。</summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    public async Task CleanDbAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var tbl in new[] { "users", "problems" })
#pragma warning disable EF1002 // 表名为硬编码常量，无注入风险
            await db.Database.ExecuteSqlRawAsync(
                $"TRUNCATE TABLE \"{tbl}\" RESTART IDENTITY CASCADE");
#pragma warning restore EF1002
    }
}
