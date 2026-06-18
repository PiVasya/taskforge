using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QuizTaskService.Data;
using QuizTaskService.Data.Entities;
using QuizTaskService.DTO;
using QuizTaskService.Services;
using static QuizTaskService.Services.Common.QuizTaskCommonService;
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Services.Access;

internal static class QuizTaskAccessService
{
    internal static Guid? TryGetUserId(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.FindFirstValue("nameid");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

}
