using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LearningContentService.Data;
using LearningContentService.Data.Entities;
using LearningContentService.DTO;
using LearningContentService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using static LearningContentService.Services.Common.LearningContentCommonService;

namespace LearningContentService.Services.Serialization;

internal static class LearningContentSerializationService
{
    internal static string? JsonOrDefault(JsonElement? element, string? json, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(json)) return json;
        if (element.HasValue && element.Value.ValueKind != JsonValueKind.Undefined)
        {
            return element.Value.GetRawText();
        }

        return fallback;
    }

}
