using System.ComponentModel.DataAnnotations;

namespace Acm.Api.Dtos;

// 上传测试点：编号 + in 文件 + out 文件（multipart/form-data）
public class TestcaseUpload
{
    [Range(1, 100000)] public int N { get; set; }     // 测试点编号
    [Required] public IFormFile InFile { get; set; } = null!;   // 输入文件
    [Required] public IFormFile OutFile { get; set; } = null!;  // 期望输出
}
