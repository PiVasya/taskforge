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
using static QuizTaskService.Services.Access.QuizTaskAccessService;
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Services.Common;

internal static class QuizTaskCommonService
{
    internal static bool CanEditQuiz(ClaimsPrincipal user)
    {
        return user.IsInRole("Admin") || user.IsInRole("LearningEditor");
    }

}
