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
using static QuizTaskService.Services.Common.QuizTaskCommonService;
using static QuizTaskService.Services.Mapping.QuizTaskMappingService;
using static QuizTaskService.Services.Serialization.QuizTaskSerializationService;

namespace QuizTaskService.Endpoints;

internal static partial class QuizTaskEndpoints
{
    internal static WebApplication MapQuizTaskEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapQuizEndpoints(app);
        MapAdminQuizEndpoints(app);
        MapAccountLifecycleInternalEndpoints(app);

        return app;
    }
}
