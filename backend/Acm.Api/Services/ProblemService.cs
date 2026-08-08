using Microsoft.EntityFrameworkCore;
using Acm.Judge.Core.Data;
using Acm.Api.Dtos;
using Acm.Judge.Core.Models;

namespace Acm.Api.Services;

public class ProblemService(AppDbContext db)
{
    public async Task<Problem> CreateAsync(ProblemCreate req, int authorId)//创建题目
    {
        if (await db.Problems.AnyAsync(p => p.Slug == req.Slug))
            throw new ApiException(409, $"slug {req.Slug} 已存在");

        var problem = new Problem // 在内存中创建 Problem 对象（此时还没进数据库）
        { 
            Slug = req.Slug, Title = req.Title, Description = req.Description, // slug/标题/题面
            InputDesc = req.InputDesc, OutputDesc = req.OutputDesc, // 输入/输出格式说明
            TimeLimit = req.TimeLimit, MemoryLimit = req.MemoryLimit, // 时间/内存限制
            Tags = req.Tags ?? [], Difficulty = req.Difficulty, // 标签（null 兜底为空数组）+ 难度
            SampleInputs = req.SampleInputs, SampleOutputs = req.SampleOutputs, // 样例输入/输出
            AuthorId = authorId, // 创建者 = 当前登录用户（从 JWT 解析出的 userId）
        };
        db.Problems.Add(problem); // 标记：把 problem 加入跟踪器，状态 = 待插入
        await db.SaveChangesAsync(); // 提交：翻译成 INSERT SQL 执行入库
        return problem; // 返回带 Id 的题目对象（SaveChanges 后 EF 已回填 Id）
    }

    // 个人自用：无 includePrivate 参数，所有登录用户可见全部题目
    public async Task<Problem> GetByIdAsync(int pid) =>//按主键 Id 查单条题目
        await db.Problems.SingleOrDefaultAsync(p => p.Id == pid)
            ?? throw new ApiException(404, "题目不存在");

    public async Task<Problem> GetBySlugAsync(string slug) =>//按 slug 精确匹配
        await db.Problems.SingleOrDefaultAsync(p => p.Slug == slug)
            ?? throw new ApiException(404, "题目不存在");

    public async Task<(List<Problem> Items, int Total)> ListAsync(
        string? keyword, string? tag, short? difficulty, int page, int size)
    {
        var q = db.Problems.AsNoTracking();//查询时不做变更跟踪，q 是一个延迟执行的查询对象（类型是 IQueryable<Problem>）
        if (!string.IsNullOrEmpty(keyword)) // keyword 为空就不加这个条件
            q = q.Where(p => EF.Functions.ILike(p.Title, $"%{keyword}%")); //标题模糊匹配（大小写不敏感）
        if (!string.IsNullOrEmpty(tag))// tag 为空就不加这个条件
            q = q.Where(p => p.Tags.Contains(tag));  //数组元素匹配
        if (difficulty.HasValue) // 难度没传就不加这个条件
            q = q.Where(p => p.Difficulty == difficulty);

        var total = await q.CountAsync();//第一次真正执行查询，只数总数，不取数据
        var items = await q.OrderBy(p => p.Id) // 按主键 Id 升序排序，保证分页顺序稳定
                           .Skip((page - 1) * size) // 跳过前 (page-1) 页的数据：第 2 页跳 20 条
                           .Take(size) // 只取一页的量：最多 size 条
                           .ToListAsync(); // 终结操作，真正执行 SQL：SELECT ... LIMIT size OFFSET (page-1)*size
        return (items, total);
    }

    public async Task<Problem> UpdateAsync(int pid, ProblemUpdate req)//更新题目
    {
        var p = await db.Problems.SingleOrDefaultAsync(x => x.Id == pid)// 查库：取出一行（被跟踪）
            ?? throw new ApiException(404, "题目不存在");

        // 部分更新：仅覆盖非 null 字段
        if (req.Title is not null)         p.Title = req.Title;
        if (req.Description is not null)   p.Description = req.Description;
        if (req.InputDesc is not null)     p.InputDesc = req.InputDesc;
        if (req.OutputDesc is not null)    p.OutputDesc = req.OutputDesc;
        if (req.TimeLimit.HasValue)        p.TimeLimit = req.TimeLimit.Value;
        if (req.MemoryLimit.HasValue)      p.MemoryLimit = req.MemoryLimit.Value;
        if (req.Tags is not null)          p.Tags = req.Tags;
        if (req.Difficulty.HasValue)       p.Difficulty = req.Difficulty;
        if (req.SampleInputs is not null)  p.SampleInputs = req.SampleInputs;
        if (req.SampleOutputs is not null) p.SampleOutputs = req.SampleOutputs;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return p;
    }

    public async Task DeleteAsync(int pid)
    {
        var p = await db.Problems.SingleOrDefaultAsync(x => x.Id == pid)
            ?? throw new ApiException(404, "题目不存在");
        db.Problems.Remove(p);
        await db.SaveChangesAsync();
    }
}
