using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

// 提交入口：POST /api/v1/problems/{pid}/submissions（异步评测，秒回 PENDING，Worker 后台执行）
[ApiController]
[Authorize]
[Route("api/v1/problems/{pid:int}/submissions")]
public class SubmissionsController(JudgeService judge) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SubmissionRead>> Submit(int pid, SubmissionCreate req) =>
        StatusCode(201, await judge.SubmitAsync(pid, this.CurrentUserId(), req));
}

// 结果查询：GET /api/v1/submissions/{sid} + 历史列表 GET /api/v1/submissions
[ApiController]
[Authorize]
[Route("api/v1/submissions")]
public class SubmissionQueryController(JudgeService judge) : ControllerBase
{
    [HttpGet("{sid:long}")]
    public async Task<ActionResult<SubmissionRead>> Get(long sid) =>
        await judge.GetAsync(sid);

    // 提交历史：仅当前用户，可选 problemId/status 筛选，分页（最新在前）
    [HttpGet]
    public async Task<ActionResult<SubmissionListResponse>> List(
        [FromQuery] int? problemId,
        [FromQuery] string? status,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int size = 20) =>
        await judge.ListAsync(this.CurrentUserId(), problemId, status, page, size);
}
