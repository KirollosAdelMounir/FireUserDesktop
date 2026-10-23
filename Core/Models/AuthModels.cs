using System;
using System.Text.Json.Serialization;

namespace UserIdentityDesktop.Core.Models;

public class LoginRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class TokenResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

public class UserData
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Email : FullName;

    public string Initials
    {
        get
        {
            var first = !string.IsNullOrWhiteSpace(FirstName) ? FirstName[0].ToString().ToUpper() : "";
            var last = !string.IsNullOrWhiteSpace(LastName) ? LastName[0].ToString().ToUpper() : "";
            var initials = $"{first}{last}";
            if (string.IsNullOrEmpty(initials) && !string.IsNullOrEmpty(Email))
                return Email[0].ToString().ToUpper();
            return string.IsNullOrEmpty(initials) ? "?" : initials;
        }
    }
}
