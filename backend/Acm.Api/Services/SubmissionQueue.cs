using StackExchange.Redis;

namespace Acm.Api.Services;

/// <summary>评测队列（Redis List：LPUSH 左进，Worker BRPOP 右出，FIFO）。</summary>
public class SubmissionQueue(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private const string Key = "queue:judge";

    public Task EnqueueAsync(long submissionId) => _db.ListLeftPushAsync(Key, submissionId);
}
