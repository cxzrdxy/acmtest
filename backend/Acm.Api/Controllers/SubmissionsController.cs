using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

// 提交入口：POST /api/v1/problems/{pid}/submissions（阻塞评测，返回完整结果）
[ApiController]
[Authorize]
[Route("api/v1/problems/{pid:int}/submissions")]
public class SubmissionsController(JudgeService judge) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SubmissionRead>> Submit(int pid, SubmissionCreate req) =>
        StatusCode(201, await judge.SubmitAsync(pid, this.CurrentUserId(), req));
}

// 结果查询：GET /api/v1/submissions/{sid}
[ApiController]
[Authorize]
[Route("api/v1/submissions")]
public class SubmissionQueryController(JudgeService judge) : ControllerBase
{
    [HttpGet("{sid:long}")]
    public async Task<ActionResult<SubmissionRead>> Get(long sid) =>
        await judge.GetAsync(sid);
}
