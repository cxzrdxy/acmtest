using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

[ApiController]
[Authorize]                       // 控制器级：所有接口需登录
[Route("api/v1/problems")]
public class ProblemsController(ProblemService svc) : ControllerBase
{
    private static ProblemRead ToRead(Models.Problem p) => new(
        p.Id, p.Slug, p.Title, p.Description, p.InputDesc, p.OutputDesc,
        p.TimeLimit, p.MemoryLimit, p.Tags, p.Difficulty,
        p.SampleInputs, p.SampleOutputs, p.AuthorId);

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
}
