using System.ComponentModel.DataAnnotations;

namespace Acm.Api.Dtos;

public record RegisterRequest(
    [Required, MaxLength(32)] string Username,
    [Required] string Password,
    // 个人自用：邮箱不做强校验，可空
    string? Email = null);

public record LoginRequest(
    [Required] string Username,
    [Required] string Password);

public record UserRead(int Id, string Username, string? Email = null);

public record TokenResponse(string AccessToken, UserRead User, string TokenType = "bearer");

public record ChangePasswordRequest(
    [Required] string OldPassword,
    [Required] string NewPassword);
