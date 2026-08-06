namespace Acm.Api.Dtos;

// 测试点列表元素：编号 + in/out 字节大小
public record TestcaseInfo(int N, long InSize, long OutSize);

public record TestcaseListResponse(List<TestcaseInfo> Items);
