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
using static LearningContentService.Services.Serialization.LearningContentSerializationService;

namespace LearningContentService.Services.Common;

internal static class LearningContentCommonService
{
    internal static bool CanEditLearning(ClaimsPrincipal user)
    {
        return user.IsInRole("Admin") || user.IsInRole("LearningEditor");
    }

    internal static IResult ApiError(int statusCode, string message, string? detail = null, string? hint = null)
    {
        return Microsoft.AspNetCore.Http.Results.Json(new
        {
            status = statusCode,
            message,
            detail,
            hint
        }, statusCode: statusCode);
    }

}
