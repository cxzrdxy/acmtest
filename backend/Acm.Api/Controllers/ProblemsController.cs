using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acm.Api.Dtos;
using Acm.Api.Services;
using Acm.Judge.Core.Models;

namespace Acm.Api.Controllers;

[ApiController]
[Authorize]                       // 控制器级：所有接口需登录
[Route("api/v1/problems")]
public class ProblemsController(ProblemService svc, TestcaseService tc) : ControllerBase
{
    private static ProblemRead ToRead(Problem p) => new(
        p.Id, p.Slug, p.Title, p.Description, p.InputDesc, p.OutputDesc,
        p.TimeLimit, p.MemoryLimit, p.Tags, p.Difficulty,
        p.SampleInputs, p.SampleOutputs, p.AuthorId,
        p.AcCount, p.SubmitCount);

    [HttpGet]
    public async Task<ActionResult<ProblemListResponse>> List(
        [FromQuery] string? keyword,
        [FromQuery] string? tag,
        [FromQuery, Range(1, 5)] short? difficulty,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int size = 20)
    {
        var (items, total) = await svc.ListAsync(keyword, tag, difficulty, page, size);
        return new ProblemListResponse(items.Select(ToRead).ToList(), total, page, size);
    }

    [HttpGet("{pid:int}")]
    public async Task<ActionResult<ProblemRead>> Get(int pid) =>
        ToRead(await svc.GetByIdAsync(pid));

    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<ProblemRead>> GetBySlug(string slug) =>
        ToRead(await svc.GetBySlugAsync(slug));

    // 个人自用：所有登录用户均可 CRUD，无需 admin 策略
    [HttpPost]
    public async Task<ActionResult<ProblemRead>> Create(ProblemCreate req)
    {
        var p = await svc.CreateAsync(req, this.CurrentUserId());
        return StatusCode(201, ToRead(p));
    }

    [HttpPut("{pid:int}")]
    public async Task<ActionResult<ProblemRead>> Update(int pid, ProblemUpdate req) =>
        ToRead(await svc.UpdateAsync(pid, req));

    [HttpDelete("{pid:int}")]
    public async Task<IActionResult> Delete(int pid)
    {
        await svc.DeleteAsync(pid);
        return NoContent();
    }

    // ---- 测试点管理（文件系统） ----

    // 列出测试点：编号 + in/out 大小
    [HttpGet("{pid:int}/testcases")]
    public ActionResult<TestcaseListResponse> ListTestcases(int pid) =>
        new TestcaseListResponse(tc.List(pid));

    // 上传单个测试点（multipart：n + inFile + outFile；同名覆盖）
    [HttpPost("{pid:int}/testcases")]
    public async Task<IActionResult> UploadTestcase(int pid, [FromForm] TestcaseUpload req, CancellationToken ct)
    {
        await svc.GetByIdAsync(pid);   // 题必须存在
        await tc.SaveAsync(pid, req, ct);
        return NoContent();
    }

    // 删除指定测试点（in + out；不存在也 204）
    [HttpDelete("{pid:int}/testcases/{n:int}")]
    public IActionResult DeleteTestcase(int pid, int n)
    {
        tc.Delete(pid, n);
        return NoContent();
    }
}
